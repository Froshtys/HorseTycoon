using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Audio;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.GameData;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Quests;
using HorseTycoon.Menus;
using HorseTycoon.Models;
using HorseTycoon.Patches;

namespace HorseTycoon
{
    /// <summary>
    /// Drives the Spring 21 Horse Festival race in Cindersap Forest.
    /// The host starts the race by talking to Pam (for betting) then the festival's host, which raises a <see cref="ReadyCheckDialog"/> on every
    /// client; once everyone accepts, each player is lined up in a fenced starting stall, a start sound plays,
    /// and when it finishes the gates open. Horses run east to the finish band, which ends the race.
    /// Local state is PerScreen; multiplayer sync uses Game1.netReady and explicit mod messages.
    /// </summary>
    public partial class FestivalRaceManager
    {
        // Registered horse festivals. Add a FestivalDefinition factory here to introduce a new festival;
        // all race/betting/ceremony/sync behavior is shared and reads from the currently active one.
        private static readonly List<FestivalDefinition> Festivals = new()
        {
            FestivalDefinition.Forest(),
            FestivalDefinition.ForestSpringTest(), // TESTING ONLY: remove when done testing.
            FestivalDefinition.FallBeach(),
            FestivalDefinition.SummerBusStop(),
        };

        /// <summary>The calendar slots (season + day + Data/Festivals asset key) for every registered horse
        /// festival. Lets the calendar patch mark these days with a horse icon without depending on
        /// Data/Festivals/FestivalDates (the away race is deliberately not registered there so it doesn't
        /// close the town). Asset key is <c>Season + Day</c>, e.g. "summer19".</summary>
        public static IReadOnlyList<(string Season, int Day, string AssetKey)> CalendarFestivals =>
            Festivals.Select(f => (f.Season, f.Day, f.Season + f.Day)).ToList();

        /// <summary>Every registered horse festival, for code outside the manager that needs their data
        /// (e.g. ModEntry injecting each festival's advance-notice letter into Data/Mail).</summary>
        internal static IReadOnlyList<FestivalDefinition> AllFestivals => Festivals;

        // Prefix for the race-start ready check. A unique suffix is appended per race attempt (see
        // BeginRace) because Game1.netReady caches checks by id until the day-start Reset() and never
        // un-readies a completed one, so reusing a fixed id makes a second race confirm instantly.
        private const string ReadyCheckPrefix = "Froshty.HorseTycoon.horseRaceStart";

        // Prefix for the ceremony-end ready check, which holds every player at the awards until all of
        // them have clicked through the host's lines so nobody is warped home mid-ceremony. Same unique-suffix
        // rule as ReadyCheckPrefix: the id is minted once by the host and shipped in StartCeremonyMessage.
        private const string CeremonyEndCheckPrefix = "Froshty.HorseTycoon.horseRaceEnd";

        /// <summary>Event id of the Summer bus-away festival (see FestivalDefinition.SummerBusStop),
        /// which the bus-ticket patches need to look up outside any active festival.</summary>
        private const string SummerFestivalEventId = "festival_summer19";

        private const float SprintCooldownMs = 10000f;
        private enum SprintPhase { Ready, Sprinting, Exhausted }

        /// <summary>How long Warrior Energy lasts after crossing the legendary sword shrine tile.</summary>
        private const float WarriorEnergyMs = 5000f;

        /// <summary>Warrior Energy's speed bonus scales off the ridden horse's Speed stat (see
        /// <see cref="HorseStats.WarriorEnergyBonus"/>). It's applied before the festival's overall speed
        /// penalty, and is entirely independent of the horse sprint (no cooldown, no exhaustion).</summary>
        private readonly PerScreen<float> warriorEnergyBonus = new(() => 0f);

        /// <summary>The "Back" layer TouchAction value marking the shrine tile that grants Warrior Energy.</summary>
        private const string WarriorEnergyTouchAction = "legendarySword";

        // Biased facing direction pool: left/right first, up/down less common.
        private static readonly int[] HorseFacingPool =
        {
            Game1.left, Game1.right, Game1.left, Game1.right, Game1.up, Game1.down,
        };

        private static readonly string[] AllSkins = { "Roan", "BlueRoan", "Dapple", "Bay", "Belgian", "Shire", "Chestnut" };
        private static readonly string[] MarnieHorseNames = {
            "Clover", "Daisy", "Biscuit", "Big Red", "Rosie", "Pepper",
            "Nutmeg", "Ember", "Cobalt", "Juniper", "Bramble", "Dusty",
        };

        private const int WanderRepickMinTicks = 60;
        private const int WanderRepickMaxTicks = 180;

        // Pixel offset applied to each rider NPC so they appear seated on the horse.
        // Negative Y moves the sprite visually upward on screen; tune if the rider looks off.
        private static readonly Vector2 RiderOffset = new(-12f, -40f);

        // The game dismounts the player as they warp into the festival, so we capture the mount each tick
        // beforehand and treat them as "arrived mounted" if they were riding within this window.
        private const int EntryMountWindowTicks = 600;

        // Delay between the last player crossing the finish line and the ceremony starting.
        private const float CeremonyDelayMs = 2000f;
        // Hard cap on total racers (players + NPCs). NPC slots are filled first-come, first-dropped.
        private const int MaxRacers = 8;

        // Lineup: riders are positioned in their stalls and the host gives the pre-race announcement; players
        // are held (no NPC movement, no finish checks) until every client clicks through and the "go" barrier
        // releases, at which point Racing begins with the countdown. Mirrors the Egg Festival cutscene→barrier.
        private enum Phase { None, Arrival, Pasture, Lineup, Racing, Finished, Ceremony, Departure }
        private enum AiMode { Normal, Match }

        private class NpcRacer
        {
            public Horse Horse = null!;
            public NPC? Rider;
            public long FakeId;
            public int TotalSpeed;
            public int TotalSprint;
            public int TotalJump;
            public Point[] Route = System.Array.Empty<Point>();
            public int WaypointIndex;
            public bool Finished;
            public SprintPhase NpcSprintPhase = SprintPhase.Ready;
            public float NpcSprintTimer;
            public float NextSprintCheckMs;
            public int LastAnimDir = -1;
            // A*-computed tile path to the current waypoint; driven by direct position updates.
            public List<Point> ComputedPath = new();
            public int PathIndex;
            // Throttle + give-up state for failed A* attempts. Without this a waypoint that A*
            // can't reach (e.g. its internal iteration cap on a long/blocked segment) makes the
            // NPC recompute a full path every frame, causing severe race-wide lag/freezes.
            public float PathRetryCooldownMs;
            public int PathRetryCount;
            public float HoofSoundTimer;
            public bool MovementDone;
            public AiMode AiMode = AiMode.Match;
            // When true, this NPC tracks the race leader instead of the nearest farmer.
            public bool MatchLeader;
            // Last multiplier applied by match AI; used to suppress redundant log lines.
            public float LastMatchMultiplier = 1f;
            // Jump arc state, active while the NPC is airborne over a jump obstacle.
            public bool IsJumping;
            public float JumpTimer;
            public float JumpDuration;
            public Vector2 JumpStart;
            public Vector2 JumpEnd;
            public float JumpPeakHeight;
            // Counts down after a forward jump lands; zone triggers are suppressed while > 0
            // so chained jumps show a visible pause on the intermediate platform.
            public float JumpCooldownMs;
            // Approach tile that last triggered a jump; suppresses re-triggering while the NPC
            // is still on that tile (prevents infinite blocked-hop loops on failed jumps).
            // Cleared as soon as the NPC moves to a different tile.
            public Point? LastJumpApproachTile;
        }

        /// <summary>Build the full 8-stall gate regardless of how many racers there actually are, so the
        /// starting line can be laid out and eyeballed at its full size.</summary>
        private static readonly bool DebugAllStalls = true;

        private readonly IModHelper Helper;
        private readonly IMonitor Monitor;
        private Texture2D? sprintBuffIcon;

        // The definition for the festival the local screen is currently in; set when leaving Phase.None
        // (EnterPasture) and cleared in Reset. Methods that run only while a festival is active read this.
        private readonly PerScreen<FestivalDefinition?> activeDef = new(() => null);
        // PenSlots shuffled once per festival entry with a deterministic per-save-per-day seed.
        // All clients compute the same shuffle since the seed is derived from shared save state.
        private readonly PerScreen<Point[]?> shuffledPenSlots = new(() => null);

        private readonly PerScreen<Phase> phase = new(() => Phase.None);
        private readonly PerScreen<Horse?> competitor = new(() => null);
        // Facing direction the local mount's gallop animation was last armed for, so we only reassign
        // on a direction change. StoppedAnimDir means "currently on the standing idle".
        private readonly PerScreen<int> localMountAnimDir = new(() => int.MinValue);
        private const int StoppedAnimDir = -1;
        private readonly PerScreen<FarmAnimal?> pastureAnimal = new(() => null);
        private readonly PerScreen<Horse?> lastRiddenMount = new(() => null);
        private readonly PerScreen<int> lastMountedTick = new(() => int.MinValue);

        // Horses the local player chose to bring on the Summer festival bus, selected in the
        // HorseBusLoadMenu at the Bus Stop. IDs are FarmAnimal.myID values; resolved to live stable
        // horses (moved) or temporary Horse instances (created) once the festival's pasture phase starts.
        private static bool SummerBusSelectionMade;
        private static readonly List<long> SummerBusHorseIds = new();
        // Farm-wide record of which player loaded each horse onto the bus (FarmAnimal ID → player ID),
        // mirrored on every client via MsgBusClaim so a horse can't board two buses. Claims are released
        // in Reset when the claimant's festival ends and cleared outright at day start.
        private static readonly Dictionary<long, long> BusHorseClaims = new();
        // Every bus-selection horse placed in the pasture on this screen (including remote players'
        // temporary copies). Temporary horses are despawned (and real stable horses sent home) in Reset.
        private readonly PerScreen<List<Horse>> busFestivalHorses = new(() => new List<Horse>());
        private readonly PerScreen<Vector2> wanderTarget = new(() => Vector2.Zero);
        private readonly PerScreen<bool> wanderMoving = new(() => false);
        private readonly PerScreen<int> wanderDir = new(() => -1);
        private readonly PerScreen<int> wanderTicks = new(() => 0);
        private readonly PerScreen<bool> readyCheckOpen = new(() => false);
        // The unique ready-check id for a race start that arrived before this screen was ready to open it,
        // or null if none is pending. Carrying the id (rather than a bool) keeps every client on the same
        // fresh netReady check: a reused id stays IsReady=true after its first completion and would make
        // the dialog confirm instantly. See <see cref="OpenRaceReadyCheck"/>.
        private readonly PerScreen<string?> pendingRaceReadyCheckId = new(() => null);
        // Id for the second "all clicked through the host's announcement" barrier (see OpenCountdownBarrier),
        // derived from the start barrier's id so every client agrees without another broadcast.
        private readonly PerScreen<string?> goBarrierId = new(() => null);
        // True once this screen has opened its go barrier for the current lineup, so UpdateLineup only opens it once.
        private readonly PerScreen<bool> goBarrierOpened = new(() => false);
        // True once the host's announcement dialogue has been shown this lineup; gates the UpdateLineup fallback so
        // it can't fire during the fade-in before the dialogue appears.
        private readonly PerScreen<bool> announcementShown = new(() => false);
        // Counts consecutive ticks the screen has been stuck fully black during festival entry.
        private readonly PerScreen<int> entryBlackTicks = new(() => 0);
        // Counts consecutive ticks the vanilla "waiting for other players" festivalStart dialog has been open.
        private readonly PerScreen<int> festivalReadyStuckTicks = new(() => 0);
        private readonly PerScreen<List<Vector2>> stallFenceTiles = new(() => new List<Vector2>());
        private readonly PerScreen<bool> stallsSpawned = new(() => false);
        private readonly PerScreen<Horse?> penHorse = new(() => null);
        private readonly PerScreen<NPC?> jasOnHorse = new(() => null);
        private readonly PerScreen<Horse?> borrowedFestivalHorse = new(() => null);
        // -1 means inactive. Kept in real time so it ticks while the festival pauses the game clock.
        private readonly PerScreen<float> startCountdown = new(() => -1f);
        private readonly PerScreen<bool> raceMusicStarted = new(() => false);
        private readonly PerScreen<List<Buff>> suppressedBuffs = new(() => new List<Buff>());
        private readonly PerScreen<SprintPhase> sprintPhase = new(() => SprintPhase.Ready);
        private readonly PerScreen<float> sprintTimer = new(() => 0f);

        // Warrior Energy: separate from the sprint state on purpose, since it never blocks or consumes a sprint.
        private readonly PerScreen<float> warriorEnergyTimer = new(() => 0f);
        // Last tile checked for the shrine, so crossing it fires once per entry instead of every tick.
        private readonly PerScreen<Vector2> lastWarriorEnergyTile = new(() => -Vector2.One);

        private readonly PerScreen<System.TimeSpan> raceStartTime = new(() => System.TimeSpan.Zero);
        private readonly PerScreen<bool> disqualified = new(() => false);

        // Bus drive-in cinematic state (Phase.Arrival). busArrivalDoorTimer >= 0 = parked, counting down to the door open.
        private readonly PerScreen<Vector2> busArrivalPos = new(() => Vector2.Zero);
        private readonly PerScreen<Vector2> busArrivalMotion = new(() => Vector2.Zero);
        private readonly PerScreen<TemporaryAnimatedSprite?> busDoorSprite = new(() => null);
        private readonly PerScreen<float> busArrivalDoorTimer = new(() => -1f);

        // Parked bus (after the arrival cinematic). The bus stays for the whole festival like the Desert's:
        // its sprites live on the festival location so they sort with the world, and stepping into its
        // doorway offers to end the festival. busExitArmed stops the drop tile from firing on arrival;
        // it only arms once the player has stepped off the doorway, mirroring vanilla touch actions.
        private readonly PerScreen<bool> busParked = new(() => false);
        private readonly PerScreen<bool> busExitArmed = new(() => false);
        private readonly PerScreen<List<TemporaryAnimatedSprite>> parkedBusSprites = new(() => new List<TemporaryAnimatedSprite>());

        // Set true while re-invoking warpFarmer from the "Yes" path so our prefix doesn't re-intercept it.
        private static bool SkipHorseWarning = false;

        private readonly PerScreen<bool> pamGreeted = new(() => false);
        // True once this player has heard the host's small talk, so later approaches go straight to
        // the "ready to race?" question.
        private readonly PerScreen<bool> starterGreeted = new(() => false);
        private readonly PerScreen<long?> betTargetFarmerId = new(() => null);
        private readonly PerScreen<string?> betTargetNpcName = new(() => null);
        private readonly PerScreen<int> betAmount = new(() => 0);
        private readonly PerScreen<bool> showBettingMoneyBox = new(() => false);

        private readonly PerScreen<List<long>> ceremonyOrder = new(() => new List<long>());
        private readonly PerScreen<int> ceremonyStep = new(() => 0);
        // Ready-check id every client shares for the end-of-ceremony barrier, and whether this screen is
        // currently sitting in it. See CeremonyEndCheckPrefix / WaitForCeremonyEnd.
        private readonly PerScreen<string?> ceremonyEndBarrierId = new(() => null);
        private readonly PerScreen<bool> ceremonyEnded = new(() => false);

        // NPC racers are the same object list for every screen; spawned once per race via guard flags.
        private readonly List<NpcRacer> npcRacers = new();
        // Tracks NPCs borrowed from their world locations so they can be restored on festival end.
        private readonly List<NPC> spawnedRiders = new();
        private bool npcRidersBorrowed = false;
        private bool npcRacersSpawned = false;
        private long nextNpcFakeId = -1L;

        // NPC racer horses shown in the pen during the pasture phase (before race start).
        private readonly List<Horse> penNpcHorses = new();
        // Decorative generated horses in Marnie's background pasture.
        private readonly List<Horse> decorativeHorses = new();
        // Subset of decorativeHorses standing in for a market offer at festivals with shops; the
        // horse is removed from the paddock once its offer is no longer available to this player.
        private readonly Dictionary<Horse, HorseOffer> marketPastureHorses = new();

        // Fresh event-actor NPCs spawned for the Racing / AwardsEvent phases.
        private readonly List<NPC> spawnedSpectators = new();
        private List<NpcSpectatorPlacement>? setupSpectators;
        private List<NpcSpectatorPlacement>? racingSpectators;
        private List<NpcSpectatorPlacement>? ceremonySpectators;

        // NPC names in the same order as the Characters tileset sprite sheet (matches Data/Characters.json insertion order).
        private static readonly string[] CharacterTileNames =
        {
            "Abigail", "Caroline", "Clint", "Demetrius", "Willy", "Elliott", "Emily", "Evelyn",
            "George", "Gus", "Haley", "Harvey", "Jas", "Jodi", "Alex", "Kent", "Leah",
            "Lewis", "Linus", "Marlon", "Marnie", "Maru", "Pam", "Penny", "Pierre",
            "Robin", "Sam", "Sebastian", "Shane", "Vincent", "Wizard", "Dwarf", "Sandy",
            "Krobus", "Leo",
        };

        // Blank tile rows prepended to SVEcharacterSheet.png / EScharacterSheet.png so indices start
        // at 144 (past the vanilla Characters sheet's 144 tiles), making loadActors ignore them safely.
        private const int SveCharacterSheetPadding = 144;
        private const int EsCharacterSheetPadding = 144;

        // NPC names in the same order as SVEcharacterSheet.png (alphabetical, matches the generator script).
        private static readonly string[] SveCharacterTileNames =
        {
            "Alesia", "Andy", "Apples", "Axel", "Brooklyn", "Camilla", "Charlie", "Chloe",
            "Claire", "Gunther", "Hank", "Henchman", "HighlandsDwarf", "Isaac", "Jace", "Jadu",
            "Jolyne", "Krobus", "Lance", "Magnus", "Marlon", "Martin", "Morgan", "Morris",
            "Olivia", "Peaches", "Scarlett", "Sophia", "Susan", "Treyvon", "Victor", "Zoey",
        };

        // NPC names in the same order as EScharacterSheet.png (alphabetical, matches the generator script).
        private static readonly string[] EsCharacterTileNames =
        {
            "Abyssrooster", "Aideen", "Beatrice", "CameronLK", "CaptainRod", "CorwinLK",
            "DaleWaede", "Duck2NPC", "DuckNPC", "EdithHart", "Eloise", "EthanHart",
            "Eyvinder", "Gremlin", "HappySlime", "Jacob", "JadeMalic", "Jasper",
            "Jessie", "JosephineK", "Juliet", "KatarynaLK", "KeanuAvis", "KennedyLK",
            "LadySheba", "LumaJunimo", "MichaelHart", "Munchboi", "OliverK", "PepperPup",
            "RichieTheMacaw", "Rosa", "StellaHart", "ToriLK", "TristanLK", "ValkyrieDog",
            "VivienneLK",
        };

        private record NpcSpectatorPlacement(string Name, Point Tile, int Direction, bool IsAutoFilled = false);

        // These NPCs are only included when at least one attending farmer has met them.
        // Kent is absent in year 1; Morgan/Scarlett are SVE; TristanLK is East Scarp.
        private static readonly HashSet<string> MetRequiredNpcNames = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "Kent", "Morgan", "Scarlett", "TristanLK",
        };

        // Holding tiles for borrowed NPCs during the pasture phase (before stall assignment).
        private static readonly Point[] NpcRiderHoldingTiles = { new(92, 20), new(92, 22), new(92, 24) };

        // PathFindController cannot be referenced by name at compile time (its [InstanceStatics]
        // attribute causes the type to be absent from the public reference surface), so we access
        // it entirely through reflection cached here at class-load time.
        private static readonly System.Type? PfcType =
            typeof(Game1).Assembly.GetTypes().FirstOrDefault(t => t.Name == "PathFindController");
        private static readonly System.Reflection.FieldInfo ControllerField =
            AccessTools.Field(typeof(Character), "controller");
        private static readonly System.Reflection.MethodInfo? PfcUpdateMethod =
            PfcType?.GetMethod("update", new[] { typeof(GameTime) });
        private static readonly System.Reflection.FieldInfo? PfcPathField =
            PfcType?.GetField("pathToEndPoint",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.ConstructorInfo? PfcCtor =
            PfcType?.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 4
                    && p[2].ParameterType == typeof(Point)
                    && p[3].ParameterType == typeof(int);
            });


        private static FestivalRaceManager? Instance;

        // Host-only: tracks finish order (UniqueMultiplayerIDs) and the 2-second ceremony delay.
        private static readonly List<long> FinishOrder = new();
        private static readonly HashSet<long> DisqualifiedFarmers = new();
        private static float HostCeremonyCountdown = -1f;

        /// <summary>True while the local racer's custom sprint is active (read by the speed patch).</summary>
        public static bool IsSprinting =>
            Instance != null && RaceRidingActive && Instance.sprintPhase.Value == SprintPhase.Sprinting;

        public FestivalRaceManager(IModHelper helper, IMonitor monitor)
        {
            this.Helper = helper;
            this.Monitor = monitor;
            Instance = this;
        }

        private const string MsgPastureHorse = "PastureHorse";
        private record PastureHorseMessage(string HorseId, int Slot);
        private const string MsgBorrowedHorse = "BorrowedHorse";
        private record BorrowedHorseMessage(string HorseId, string Skin, int Slot);

        // Announces a temporary bus horse (a barn horse with no live stable Horse entity) so other
        // clients can spawn their own local copy in the pasture.
        private const string MsgBusHorse = "BusHorse";
        private record BusHorseMessage(string HorseId, string Name, string Skin, string Overlays, int Slot);

        // Claims (or releases) horses for a player's bus trip so other players' loading menus hide them.
        private const string MsgBusClaim = "BusClaim";
        private record BusClaimMessage(List<long> AnimalIds, long PlayerId, bool Release);
        private const string MsgOpenReadyCheck = "OpenReadyCheck";
        private const string MsgPlayerFinished = "PlayerFinished";
        private const string MsgPlayerDisqualified = "PlayerDisqualified";
        private const string MsgStartCeremony = "StartCeremony";
        private record StartCeremonyMessage(List<long> RankedPlayerIds, string EndBarrierId);
        private const string MsgNpcSprint = "NpcSprint";
        private record NpcSprintMessage(int NpcIndex, float DurationMs);

        // Announces the farm's first Spring Horse Festival win so every other player gets
        // Lewis's news letter naming the winner.
        private const string MsgSpringWin = "SpringFestivalWin";
        private record SpringWinMessage(string WinnerName);

        // Custom start sound registered as a Data/AudioChanges cue in [CP] HorseTycoon/data/sound.json.
        private const string RaceStartSoundCue = "CP.HorseTycoon_RaceStart";
        private const int RaceStartSoundMs = 8000;
        // Global-fade speed for the transition into the race lineup (vanilla default; lower = slower).
        private const float RaceFadeSpeed = 0.02f;

        public void Initialize()
        {
            sprintBuffIcon = this.Helper.ModContent.Load<Texture2D>("assets/HorseRunningBuff.png");
            this.Helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            this.Helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            this.Helper.Events.Display.RenderedHud += this.OnRenderedHud;
            this.Helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
            this.Helper.Events.Multiplayer.ModMessageReceived += this.OnMessageReceived;
            this.Helper.Events.Multiplayer.PeerConnected += this.OnPeerConnected;
            this.Helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            this.Helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;

            // isRidingHorse() forces false during ANY event, suppressing mount drawing, riding pose, and
            // horse speed. Re-enable it while mounted inside our festival.
            var harmony = new Harmony("Froshty.HorseTycoon.FestivalRace");
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.isRidingHorse)),
                postfix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(IsRidingHorse_Postfix)));

            // getMovementSpeed zeroes the horse bonus / addedSpeed during events. Report non-event during
            // our race so riding speed, SpeedBoost stat, and the sprint buff all apply normally.
            // A postfix then applies a 20% speed penalty for the duration of the festival.
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.getMovementSpeed)),
                transpiler: new HarmonyMethod(typeof(FestivalRaceManager), nameof(GetMovementSpeed_Transpiler)),
                postfix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(GetMovementSpeed_Postfix)));

            // Block dismounting during the race. Patching checkAction (which initiates the dismount slide)
            // rather than dismount() prevents the rider getting stuck mid-slide.
            harmony.Patch(
                original: AccessTools.Method(typeof(Horse), nameof(Horse.checkAction)),
                prefix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(CheckAction_Prefix)));

            // Intercept warps to Forest during the festival window so we can warn the player if
            // they're not mounted and an available horse exists on the farm.
            // We patch the LocationRequest overload because ALL string overloads delegate into it,
            // and the festival attendance gate (SP dialog + MP ReadyCheckDialog "festivalStart") lives
            // inside that overload, so this prefix fires before any of that logic runs.
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), "warpFarmer",
                    new[] { typeof(LocationRequest), typeof(int), typeof(int), typeof(int) }),
                prefix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(WarpFarmer_Prefix)));

            // On the Summer Horse Festival day, clicking the bus at the real Bus Stop sells a ticket to the
            // festival grounds (a custom location) instead of running the vanilla desert bus.
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.Locations.BusStop), nameof(StardewValley.Locations.BusStop.checkAction)),
                prefix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(BusStopCheckAction_Prefix)));

            // After boarding the bus for the Summer festival, redirect the bus's destination from the Desert
            // to the festival grounds (the private busLeftToDesert runs once the bus animates off-screen).
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.Locations.BusStop), "busLeftToDesert"),
                prefix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(BusLeftToDesert_Prefix)));

            // At a bus festival the only way home is the bus, so suppress vanilla's "leave the festival?"
            // prompt at the map edge (the player just bumps the boundary instead).
            harmony.Patch(
                original: AccessTools.Method(typeof(Event), nameof(Event.TryStartEndFestivalDialogue)),
                prefix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(TryStartEndFestivalDialogue_Prefix)));


            this.Helper.ConsoleCommands.Add(
                "ht_race_tile",
                "Logs the player's current tile (for tuning festival race coordinates).",
                (_, _) => this.Monitor.Log(
                    $"Player tile: {Game1.player.Tile} | mounted: {Game1.player.isRidingHorse()} | location: {Game1.currentLocation?.Name}",
                    LogLevel.Info));

            this.Helper.ConsoleCommands.Add(
                "ht_run_state",
                "Dumps every field that decides whether the player runs or walks (for the festival walking bug).",
                (_, _) =>
                {
                    Farmer p = Game1.player;
                    Event? ev = Game1.currentLocation?.currentEvent;
                    this.Monitor.Log(
                        $"location={Game1.currentLocation?.Name} | event={ev?.id} isFestival={ev?.isFestival} playerControlSeq={ev?.playerControlSequence}\n"
                        + $"running={p.running} speed={p.Speed} addedSpeed={p.addedSpeed} moveSpeed={p.getMovementSpeed()}\n"
                        + $"canOnlyWalk={p.canOnlyWalk} CanMove={p.CanMove} mount={(p.mount != null)} stamina={p.stamina}\n"
                        + $"options.autoRun={Game1.options.autoRun} runButton={string.Join(",", Game1.options.runButton)} gamepad={Game1.options.gamepadControls}\n"
                        + $"eventUp={Game1.eventUp} freezeControls={Game1.freezeControls} phase={phase.Value} lastBail={lastRunStateBail ?? "(none)"}",
                        LogLevel.Info);
                });

            this.Helper.ConsoleCommands.Add(
                "ht_race_restart",
                "Restarts the horse festival race from the pasture phase. Works from any race phase.",
                (_, _) => this.RestartRace());

            this.Helper.ConsoleCommands.Add(
                "ht_mod_npcs",
                "Dumps mod NPC detection state for debugging placeholder spectator slots.",
                (_, _) =>
                {
                    var vanillaNames = new HashSet<string>(CharacterTileNames, System.StringComparer.OrdinalIgnoreCase);
                    if (Game1.characterData == null)
                    {
                        this.Monitor.Log("Game1.characterData is NULL", LogLevel.Error);
                        return;
                    }
                    this.Monitor.Log($"Game1.characterData has {Game1.characterData.Count} entries.", LogLevel.Info);
                    var modNames = Game1.characterData.Keys.Where(n => !vanillaNames.Contains(n)).ToList();
                    this.Monitor.Log($"Mod NPC names ({modNames.Count}): {string.Join(", ", modNames)}", LogLevel.Info);
                    var farmers = Game1.getAllFarmers().ToList();
                    this.Monitor.Log($"Online farmers: {string.Join(", ", farmers.Select(f => f.Name))}", LogLevel.Info);
                    foreach (string name in modNames)
                    {
                        bool anyMet = farmers.Any(f => f.friendshipData.ContainsKey(name));
                        this.Monitor.Log($"  '{name}': met by any farmer = {anyMet}", LogLevel.Info);
                    }
                    var met = this.GetMetModNpcNames();
                    this.Monitor.Log($"Final met list ({met.Count}): {string.Join(", ", met)}", LogLevel.Info);
                });

            this.Helper.ConsoleCommands.Add(
                "ht_race_pfc_info",
                "Dumps PathFindController reflection info for debugging NPC pathfinding.",
                (_, _) =>
                {
                    if (PfcType == null) { this.Monitor.Log("PfcType: NULL", LogLevel.Error); return; }
                    this.Monitor.Log($"PfcType: {PfcType.FullName}", LogLevel.Info);
                    foreach (var ctor in PfcType.GetConstructors())
                    {
                        var parms = string.Join(", ", System.Array.ConvertAll(ctor.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                        this.Monitor.Log($"  ctor({parms})", LogLevel.Info);
                    }
                    var upd = PfcType.GetMethod("update", new[] { typeof(GameTime) });
                    this.Monitor.Log($"PfcUpdateMethod: {(upd == null ? "NULL" : upd.ToString())}", LogLevel.Info);
                });
        }

        /// <summary>True while the local player is mounted and the festival is in its Racing or Finished phase.</summary>
        public static bool RaceRidingActive =>
            RaceFestival != null
            && (Instance?.phase.Value == Phase.Racing || Instance?.phase.Value == Phase.Finished)
            && Game1.player?.mount != null;

        /// <summary>True when the active festival takes place on the beach, where water landing is permitted.</summary>
        public static bool IsBeachFestivalActive =>
            RaceRidingActive && Instance?.activeDef.Value?.LocationName == "Beach";

        /// <summary>True while the start countdown is running (rider is penned in the stall).</summary>
        public static bool IsStartCountdownActive =>
            Instance != null && Instance.startCountdown.Value >= 0f;

        private static void IsRidingHorse_Postfix(Farmer __instance, ref bool __result)
        {
            if (!__result && __instance?.mount != null && RaceFestival != null)
                __result = true;
        }

        private static bool CheckAction_Prefix(Horse __instance, Farmer who, ref bool __result)
        {
            if (RaceRidingActive && __instance.rider == Game1.player && who == Game1.player)
            {
                __result = true;
                return false;
            }
            return true;
        }

        /// <summary>Used by the getMovementSpeed transpiler: report "not in an event" for the whole time
        /// our festival is running (not just while actively racing), so players can run on foot while
        /// browsing the pasture/shops too. Other vanilla events (weddings, other festivals) are unaffected.</summary>
        public static bool EventUpForSpeed() => Game1.eventUp && RaceFestival == null;

        /// <summary>True whenever the horse festival is in any of the racing phases).</summary>
        public static bool IsInAnyRacingPhase => Instance?.phase.Value is Phase.Racing or Phase.Finished;

        /// <summary>True while the local player is carrying the Warrior Energy pickup.</summary>
        public static bool HasWarriorEnergy => Instance != null && Instance.warriorEnergyTimer.Value > 0f;

        private static void GetMovementSpeed_Postfix(Farmer __instance, ref float __result)
        {
            // Added before the festival penalty below, so the pickup is worth its full raw speed.
            if (__instance.IsLocalPlayer && HasWarriorEnergy)
                __result += Instance!.warriorEnergyBonus.Value;

            // Going is applied AFTER the festival penalty so it lands as a true flat +1/-1 on the speed
            // the rider actually gets. It reads __instance rather than Game1.player because this also
            // runs for remote farmers via NetPosition.UpdateExtrapolation.
            if (IsInAnyRacingPhase)
                __result = System.Math.Max(MinRaceSpeed, __result * 0.75f + GoingSpeedBonusAt(__instance.Tile));
        }

        private static IEnumerable<CodeInstruction> GetMovementSpeed_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo eventUpField = AccessTools.Field(typeof(Game1), nameof(Game1.eventUp));
            MethodInfo helper = AccessTools.Method(typeof(FestivalRaceManager), nameof(EventUpForSpeed));
            foreach (CodeInstruction instr in instructions)
            {
                if (instr.LoadsField(eventUpField))
                    yield return new CodeInstruction(OpCodes.Call, helper);
                else
                    yield return instr;
            }
        }

        /// <summary>The active Horse Festival event (any registered festival), or null if we're not in one.</summary>
        private static Event? RaceFestival
        {
            get
            {
                Event? ev = Game1.currentLocation?.currentEvent;
                return ev != null && ev.isFestival && Festivals.Any(f => f.EventId == ev.id) ? ev : null;
            }
        }

        /// <summary>The definition for the festival the active screen is currently in. Only valid while a
        /// festival is running (phase != None); the manager only touches layout/routes in that window.</summary>
        private static FestivalDefinition Def => Instance!.activeDef.Value!;
        private static Point[] ShuffledPenSlots => Instance!.shuffledPenSlots.Value ?? Def.PenSlots;

        /// <summary>The registered festival definition matching the given event, or null.</summary>
        private static FestivalDefinition? DefinitionForEvent(Event? ev) =>
            ev == null ? null : Festivals.FirstOrDefault(f => f.EventId == ev.id);

        /// <summary>The registered festival whose date/time window is open right now, or null.</summary>
        private static FestivalDefinition? CurrentWindowFestival()
        {
            foreach (FestivalDefinition def in Festivals)
            {
                if (Game1.currentSeason == def.Season
                    && Game1.dayOfMonth == def.Day
                    && Game1.timeOfDay >= def.StartTime
                    && Game1.timeOfDay < def.EndTime)
                    return def;
            }
            return null;
        }

        // ======================== Festival Entry Horse Warning ========================

        /// <summary>
        /// Fires before Game1.warpFarmer(LocationRequest, int, int, int), the single overload that all
        /// string convenience warpFarmer overloads delegate into, and where the festival attendance gate
        /// (SP dialog / MP ReadyCheckDialog "festivalStart") lives. Cancels the warp to Forest during the
        /// festival window if the player is not mounted and has a rideable horse on the farm, then shows
        /// a yes/no confirmation. "Yes" re-invokes warpFarmer with a bypass flag; "No" does nothing.
        /// </summary>
        private static bool WarpFarmer_Prefix(LocationRequest locationRequest, int tileX, int tileY, int facingDirectionAfterWarp)
        {
            if (SkipHorseWarning) return true;
            FestivalDefinition? def = CurrentWindowFestival();
            if (def == null) return true;
            if (locationRequest?.Name != def.LocationName) return true;
            if (Game1.player?.mount != null) return true;
            if (!HasAvailableUnmountedHorse()) return true;

            var capturedReq = locationRequest;
            int capturedX = tileX, capturedY = tileY, capturedDir = facingDirectionAfterWarp;

            Game1.currentLocation.createQuestionDialogue(
                "Are you sure you'd like to enter the horse festival without your horse?",
                new[]
                {
                    new Response("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                    new Response("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
                },
                (_, answer) =>
                {
                    if (answer != "Yes") return;
                    SkipHorseWarning = true;
                    Game1.warpFarmer(capturedReq, capturedX, capturedY, capturedDir);
                    SkipHorseWarning = false;
                });

            return false; // cancel the original warp
        }

        /// <summary>Returns true if at least one farm horse exists that is not currently mounted by any player.</summary>
        private static bool HasAvailableUnmountedHorse()
        {
            var mountedIds = Game1.getOnlineFarmers()
                .Where(f => f.mount != null)
                .Select(f => f.mount!.HorseId)
                .ToHashSet();

            bool found = false;
            Utility.ForEachLocation(loc =>
            {
                foreach (NPC npc in loc.characters.ToList())
                {
                    if (npc is Horse horse
                        && !mountedIds.Contains(horse.HorseId)
                        && HorseHelper.GetFarmAnimalForHorse(horse) != null)
                    {
                        found = true;
                        return false;
                    }
                }
                return true;
            });
            return found;
        }

        // ======================== End Festival Entry Horse Warning ========================

        // ======================== Summer Festival Bus Ticket ========================

        /// <summary>Gold cost of a ticket to the Summer Horse Festival (matches the vanilla desert bus fare).</summary>
        private const int SummerTicketPrice = 500;

        /// <summary>How many horses fit on the bus to the Summer Horse Festival (2/4/8 by trailer tier).</summary>
        private static int BusHorseCapacity => BusTrailerManager.HorseCapacity;

        /// <summary>
        /// Fires before <see cref="StardewValley.Locations.BusStop.checkAction"/>. On the Summer Horse Festival
        /// day, clicking the bus door tile (Buildings index 1057) sells a festival ticket and warps the player to
        /// the festival grounds instead of running the vanilla desert bus. Gated on the bus being repaired
        /// (<c>ccVault</c> mail). Returns false to cancel the vanilla behavior for that click.
        /// </summary>
        private static bool BusStopCheckAction_Prefix(StardewValley.Locations.BusStop __instance, xTile.Dimensions.Location tileLocation, ref bool __result)
        {
            FestivalDefinition? def = Festivals.FirstOrDefault(f => f.EventId == SummerFestivalEventId);
            if (def == null) return true;
            if (Game1.currentSeason != def.Season || Game1.dayOfMonth != def.Day) return true;
            if (__instance.getTileIndexAt(tileLocation, "Buildings", "outdoors") != 1057) return true;

            OfferSummerFestivalTicket(__instance, def);
            __result = true;
            return false; // cancel vanilla desert bus
        }

        /// <summary>True between boarding the bus for the Summer festival and the bus leaving the screen, so
        /// <see cref="BusLeftToDesert_Prefix"/> can redirect the destination from the Desert to the festival.</summary>
        private static bool BoardingForSummerFestival;

        /// <summary>True when Pam offered the local player a free ride because they couldn't afford the
        /// ticket; skips the fare charge in <see cref="BoardBusToSummerFestival"/>.</summary>
        private static bool SummerFareWaived;

        /// <summary>Ready-check id for the bus departure: everyone must buy a ticket before it leaves.</summary>
        private const string BusReadyCheckName = "Froshty.HorseTycoon.summerBusDeparture";

        /// <summary>Shows the gated ticket prompt and, on purchase, boards the bus (with the vanilla drive-off
        /// animation) bound for the festival grounds.</summary>
        private static void OfferSummerFestivalTicket(GameLocation bus, FestivalDefinition def)
        {
            if (!Game1.MasterPlayer.mailReceived.Contains("ccVault"))
            {
                Game1.drawObjectDialogue("The bus isn't running yet. The road to the festival opens once the bus has been repaired.");
                return;
            }
            if (!BusTrailerManager.IsBuilt)
            {
                Game1.drawObjectDialogue("The bus can't haul horses without a trailer. Maybe Robin could build one...");
                return;
            }
            if (Game1.timeOfDay < def.StartTime)
            {
                Game1.drawObjectDialogue("The bus to the Summer Horse Festival leaves at noon.");
                return;
            }
            if (Game1.timeOfDay >= def.EndTime)
            {
                Game1.drawObjectDialogue("The bus has made its last trip to the Summer Horse Festival for today.");
                return;
            }

            string price = Utility.getNumberWithCommas(SummerTicketPrice);
            bus.createQuestionDialogue(
                $"Buy a ticket to the Summer Horse Festival? ({price}g)",
                bus.createYesNoResponses(),
                (Farmer _, string answer) =>
                {
                    if (answer != "Yes") return;

                    // Can't afford it? Pam waves them aboard for free.
                    if (Game1.player.Money < SummerTicketPrice)
                    {
                        SummerFareWaived = true;
                        string pamLine = "Short on cash, hon? Don't sweat it... I'm driving out there anyway. Hop on, this one's on me.$h";
                        NPC? pam = Game1.getCharacterFromName("Pam");
                        if (pam != null)
                            Game1.DrawDialogue(new Dialogue(pam, null, pamLine));
                        else
                            Game1.drawObjectDialogue("Pam waves you aboard: \"Don't sweat the fare, hon. This one's on me.\"");
                        Game1.afterDialogues = () => OpenBusHorseSelection(bus);
                        return;
                    }

                    SummerFareWaived = false;
                    // Pick which horses (up to the bus capacity) come along before boarding; closing
                    // the menu without departing aborts the trip with no charge.
                    OpenBusHorseSelection(bus);
                });
        }

        /// <summary>Opens the horse-loading menu listing every rideable horse the player could bring
        /// (barn horses plus stable horses not ridden by someone else). Confirming stores the selection
        /// for <see cref="PlaceBusSelectionInPasture"/> and boards the bus; closing the menu cancels.</summary>
        private static void OpenBusHorseSelection(GameLocation bus)
        {
            FarmAnimal? mountAnimal = Game1.player.mount != null
                ? HorseHelper.GetFarmAnimalForHorse(Game1.player.mount)
                : null;

            List<FarmAnimal> horses = HorseHelper.GetAllBarnHorses()
                .Where(a => !IsClaimedByOtherPlayer(a.myID.Value))
                .Where(a => !HorseHelper.IsHidden(a)
                    || (mountAnimal != null && a.myID.Value == mountAnimal.myID.Value)
                    || IsStableAnimalAvailable(a))
                .OrderByDescending(a => mountAnimal != null && a.myID.Value == mountAnimal.myID.Value)
                .ThenBy(a => a.Name)
                .ToList();

            SummerBusHorseIds.Clear();
            SummerBusSelectionMade = false;

            if (horses.Count == 0)
            {
                BeginBusDepartureWait(bus);
                return;
            }

            var preselected = new List<long>();
            if (mountAnimal != null)
                preselected.Add(mountAnimal.myID.Value);

            Game1.activeClickableMenu = new HorseBusLoadMenu(horses, preselected, BusHorseCapacity, selected =>
            {
                // Another player may have claimed a horse while this menu was open, so abort (no charge)
                // so the player can re-pick from the refreshed list.
                FarmAnimal? taken = selected.FirstOrDefault(a => IsClaimedByOtherPlayer(a.myID.Value));
                if (taken != null)
                {
                    Game1.showRedMessage($"{taken.Name} has already been taken to the festival");
                    return;
                }

                SummerBusHorseIds.Clear();
                foreach (FarmAnimal animal in selected)
                    SummerBusHorseIds.Add(animal.myID.Value);
                SummerBusSelectionMade = true;
                ClaimBusHorses();

                // Leaving the ridden horse behind: dismount so it stays at the Bus Stop.
                if (mountAnimal != null && !SummerBusHorseIds.Contains(mountAnimal.myID.Value))
                    Game1.player.mount?.dismount();

                BeginBusDepartureWait(bus);
            });
        }

        /// <summary>In multiplayer the bus doesn't leave until every online player has bought their
        /// ticket (mirrors the walk-in festivals' entry wait): a ready check holds each player after
        /// their purchase and boards everyone simultaneously once the last one confirms. Cancelling
        /// unreadies the player; their ticket isn't charged until boarding actually starts.</summary>
        private static void BeginBusDepartureWait(GameLocation bus)
        {
            if (!Game1.IsMultiplayer)
            {
                BoardBusToSummerFestival(bus);
                return;
            }

            Game1.activeClickableMenu = new ReadyCheckDialog(
                BusReadyCheckName,
                allowCancel: true,
                onConfirm: (_) =>
                {
                    Game1.exitActiveMenu();
                    BoardBusToSummerFestival(bus);
                },
                onCancel: (_) =>
                {
                    Game1.netReady.SetLocalReady(BusReadyCheckName, ready: false);
                });
        }

        /// <summary>Animal IDs of every horse currently loaded onto the festival bus (any player's),
        /// used by <see cref="BusTrailerManager"/> to show them through the trailer windows. The local
        /// player's horses come first (in the order they were picked) so their own horses fill the
        /// visible window slots.</summary>
        public static List<long> GetBusTrailerHorseIds()
        {
            if (BusHorseClaims.Count == 0)
                return new List<long>();
            long localId = Game1.player.UniqueMultiplayerID;
            return BusHorseClaims
                .OrderByDescending(kv => kv.Value == localId)
                .ThenBy(kv => SummerBusHorseIds.IndexOf(kv.Key))
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary>True when another player has already loaded this horse onto their bus today.</summary>
        private static bool IsClaimedByOtherPlayer(long animalId) =>
            BusHorseClaims.TryGetValue(animalId, out long playerId)
            && playerId != Game1.player.UniqueMultiplayerID;

        /// <summary>Records the local player's bus selection in the claim table and broadcasts it so the
        /// horses disappear from every other player's loading menu.</summary>
        private static void ClaimBusHorses()
        {
            if (Instance == null)
                return;
            long playerId = Game1.player.UniqueMultiplayerID;

            // Drop claims from an earlier selection this player abandoned (e.g. cancelled the departure
            // wait and re-picked) so deselected horses free up for everyone else.
            List<long> stale = BusHorseClaims
                .Where(kv => kv.Value == playerId && !SummerBusHorseIds.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToList();
            foreach (long animalId in stale)
                BusHorseClaims.Remove(animalId);

            foreach (long animalId in SummerBusHorseIds)
                BusHorseClaims[animalId] = playerId;

            if (Game1.IsMultiplayer)
            {
                if (stale.Count > 0)
                {
                    Instance.Helper.Multiplayer.SendMessage(
                        new BusClaimMessage(stale, playerId, Release: true),
                        MsgBusClaim,
                        modIDs: new[] { Instance.Helper.ModRegistry.ModID });
                }
                if (SummerBusHorseIds.Count > 0)
                {
                    Instance.Helper.Multiplayer.SendMessage(
                        new BusClaimMessage(new List<long>(SummerBusHorseIds), playerId, Release: false),
                        MsgBusClaim,
                        modIDs: new[] { Instance.Helper.ModRegistry.ModID });
                }
            }
        }

        /// <summary>True for a hidden (stable-assigned) FarmAnimal whose live Horse exists and isn't
        /// currently being ridden by another farmer, so it can be loaded onto the bus.</summary>
        private static bool IsStableAnimalAvailable(FarmAnimal animal)
        {
            if (!HorseHelper.IsHidden(animal)) return false;
            Horse? horse = FindStableHorseForAnimal(animal.myID.Value);
            return horse != null
                && !Game1.getOnlineFarmers().Any(f => f != Game1.player && f.mount?.HorseId == horse.HorseId);
        }

        /// <summary>Finds the live stable Horse entity backing a hidden FarmAnimal, if any.</summary>
        private static Horse? FindStableHorseForAnimal(long animalId)
        {
            foreach (Stable stable in Game1.getFarm().buildings.OfType<Stable>())
            {
                if (stable.modData.TryGetValue(HorseHelper.CurrentFarmHorseIdKey, out string idStr)
                    && long.TryParse(idStr, out long id) && id == animalId)
                    return stable.getStableHorse();
            }
            return null;
        }

        /// <summary>Charges the fare and replicates the vanilla bus boarding sequence (walk to the door, bus
        /// drives off) so the same departure animation plays, then redirects to the festival via
        /// <see cref="BusLeftToDesert_Prefix"/>.</summary>
        private static void BoardBusToSummerFestival(GameLocation busLoc)
        {
            if (busLoc is not StardewValley.Locations.BusStop bus)
                return;

            // Pam waives the fare for players who couldn't afford the ticket.
            if (!SummerFareWaived)
            {
                if (Game1.player.Money < SummerTicketPrice)
                {
                    Game1.drawObjectDialogue(Game1.content.LoadString("Strings\\Locations:BusStop_NotEnoughMoneyForTicket"));
                    return;
                }
                Game1.player.Money -= SummerTicketPrice;
                Game1.playSound("purchaseClick");
            }
            SummerFareWaived = false;
            BoardingForSummerFestival = true;

            // Mirror BusStop.answerDialogue's "Bus_Yes" branch so the vanilla drive-off animation runs.
            Game1.freezeControls = true;
            Game1.viewportFreeze = true;
            AccessTools.Field(typeof(StardewValley.Locations.BusStop), "forceWarpTimer").SetValue(bus, 8000);
            var endBehavior = (StardewValley.Pathfinding.PathFindController.endBehavior)System.Delegate.CreateDelegate(
                typeof(StardewValley.Pathfinding.PathFindController.endBehavior),
                bus,
                AccessTools.Method(typeof(StardewValley.Locations.BusStop), "playerReachedBusDoor"));
            Game1.player.controller = new StardewValley.Pathfinding.PathFindController(
                Game1.player, bus, new Point(22, 9), 0, endBehavior);
            Game1.player.setRunning(isRunning: true);
            if (Game1.player.mount != null)
                Game1.player.mount.farmerPassesThrough = true;
        }

        /// <summary>
        /// Fires before the private <c>BusStop.busLeftToDesert</c>. When the player boarded for the Summer
        /// festival, redirect the destination from the Desert to the festival grounds (which triggers the
        /// festival event), keeping the vanilla drive-off animation. Returns false to skip the Desert warp.
        /// Also holds the departure (festival or Desert) while the trailer is still driving off screen:
        /// vanilla warps as soon as the bus body clears the edge, which would cut with the trailer mid-screen.
        /// </summary>
        private static bool BusLeftToDesert_Prefix(StardewValley.Locations.BusStop __instance)
        {
            if (BusTrailerManager.TryDelayDepartureForTrailer(__instance))
                return false;

            if (!BoardingForSummerFestival)
                return true;
            BoardingForSummerFestival = false;

            FestivalDefinition? def = Festivals.FirstOrDefault(f => f.EventId == SummerFestivalEventId);
            if (def == null)
                return true;

            Game1.viewportFreeze = true;
            SkipHorseWarning = true; // horse choice was already made in the bus loading menu
            Game1.warpFarmer(def.LocationName, 34, 23, 0);
            SkipHorseWarning = false;
            Game1.globalFade = false;
            return false;
        }

        /// <summary>
        /// Fires before <see cref="Event.TryStartEndFestivalDialogue"/>, vanilla's "leave the festival?"
        /// prompt when a player walks off the edge of the festival map. At a bus festival the bus is the
        /// only ride home (and vanilla's prompt would drop them at the farm without the departure
        /// cinematic or the horse handback), so this suppresses it and the map edge just blocks.
        /// </summary>
        private static bool TryStartEndFestivalDialogue_Prefix(Event __instance, ref bool __result)
        {
            if (DefinitionForEvent(__instance)?.BusArrival != true)
                return true;

            Game1.player.Halt();
            Game1.player.Position = Game1.player.lastPosition;
            __result = true; // "handled": stops the caller from falling through to other exit handling
            return false;
        }

        // ======================== End Summer Festival Bus Ticket ========================

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            // Rescue a straggler stuck on the vanilla "waiting for other players" gate. Runs before the
            // RaceFestival check below because this fires pre-warp, while the player is still outside.
            this.GuardStuckFestivalReadyCheck();

            // Capture the ridden horse every tick before the festival dismounts the player on warp.
            if (Game1.player.isRidingHorse() && Game1.player.mount != null)
            {
                lastRiddenMount.Value = Game1.player.mount;
                lastMountedTick.Value = Game1.ticks;
            }

            Event? festival = RaceFestival;

            if (festival == null)
            {
                if (phase.Value != Phase.None)
                    this.Reset();
                return;
            }

            if (!festival.playerControlSequence)
            {
                this.GuardEntryFade();
                return;
            }
            entryBlackTicks.Value = 0;
            MaintainRunState();
            this.UpdateWarriorEnergy();

            switch (phase.Value)
            {
                case Phase.None:
                    if (DefinitionForEvent(festival)?.BusArrival == true)
                        this.EnterBusArrival(festival);
                    else
                        this.EnterPasture(festival);
                    break;
                case Phase.Arrival:
                    this.UpdateBusArrival(festival);
                    break;
                case Phase.Departure:
                    this.UpdateBusDeparture(festival);
                    break;
                case Phase.Pasture:
                    this.UpdatePasture();
                    AdvanceHorseAnimations();
                    break;
                case Phase.Lineup:
                    this.UpdateLineup();
                    AdvanceHorseAnimations();
                    break;
                case Phase.Racing:
                    this.UpdateStartCountdown();
                    // animateOnce is gated on !Game1.eventUp, so we advance all horses ourselves.
                    AdvanceHorseAnimations();
                    this.UpdateHoofSounds();
                    // Check player finish first so the player wins any same-tick tie with an NPC.
                    this.CheckFinish();
                    this.CheckDisqualification();
                    this.UpdateNpcRacers();
                    break;
                case Phase.Finished:
                    AdvanceHorseAnimations();
                    this.UpdateHoofSounds();
                    this.UpdateNpcRacers();
                    if (disqualified.Value)
                    {
                        // The festival event re-enables fadeToBlack every tick; suppress it continuously.
                        Game1.fadeToBlack = false;
                        Game1.fadeToBlackAlpha = 0f;
                        // Horse.update re-enables CanMove every tick while mounted; counteract it.
                        Game1.player.CanMove = false;
                    }
                    break;
                case Phase.Ceremony:
                    AdvanceHorseAnimations();
                    this.EnforceCeremonyFacing();
                    this.UpdateCeremony();
                    // Nobody wanders off mid-ceremony. Horse.update re-enables CanMove every tick while
                    // mounted, so this has to be re-applied rather than set once at StartCeremony. Stops
                    // at the end so EndFestival's handback of control survives into the walk home.
                    if (!ceremonyEnded.Value)
                        Game1.player.CanMove = false;
                    break;
            }

            // Host ticks the 2-second ceremony delay after all players have finished.
            if (IsHost && HostCeremonyCountdown >= 0f)
            {
                HostCeremonyCountdown -= Game1.currentGameTime.ElapsedGameTime.Milliseconds;
                if (HostCeremonyCountdown <= 0f)
                {
                    HostCeremonyCountdown = -1f;
                    this.BroadcastCeremony();
                }
            }

            if (RaceRidingActive)
                this.UpdateSprint();
        }

        /// <summary>Keeps the on-foot player's run state alive for the length of the festival.
        /// Farmer.setMoving (0x40) clears Farmer.running on every full stop, and inside one long festival
        /// event none of vanilla's restore points fire again (GameLocation.resetLocalState only runs on
        /// location entry; Game1.checkForRunButton only on a run-key press/release edge), so with Auto-Run
        /// on the player silently drops to a walk. Mounted riders are unaffected: riding speed and the
        /// riding pose don't read Farmer.running at all.</summary>
        private static void MaintainRunState()
        {
            Farmer player = Game1.player;
            if (player.mount != null || !player.CanMove || player.stamina <= 0f)
            {
                LogRunStateBail(player.mount != null ? "mounted" : (!player.CanMove ? "CanMove=false" : "stamina<=0"));
                return;
            }

            // Defensive: the string-script Event ctor sets this for cutscenes. Festivals load through the
            // parameterless ctor so it should already be false, but it hard-blocks setRunning if ever set.
            if (player.canOnlyWalk && !player.bathingClothes.Value)
            {
                Logger.LogVerbose("MaintainRunState: clearing canOnlyWalk");
                player.canOnlyWalk = false;
            }
            if (player.canOnlyWalk)
            {
                LogRunStateBail("canOnlyWalk (bathing clothes)");
                return;
            }

            bool runKeyHeld =
                Game1.isOneOfTheseKeysDown(Game1.GetKeyboardState(), Game1.options.runButton)
                || (Game1.options.gamepadControls && !Game1.options.autoRun
                    && System.Math.Abs(Vector2.Distance(Game1.input.GetGamePadState().ThumbSticks.Left, Vector2.Zero)) > 0.9f);
            bool shouldRun = Game1.options.autoRun != runKeyHeld;

            if (player.running == shouldRun)
            {
                LogRunStateBail(null);
                return;
            }

            player.setRunning(shouldRun);

            // If setRunning was rejected, Farmer.running is still wrong, so say so, with the fields that gate it.
            if (player.running != shouldRun)
            {
                LogRunStateBail($"setRunning({shouldRun}) REJECTED: speed={player.Speed}, isEating={player.isEating}, " +
                    $"UsingTool={player.UsingTool}, pauseAnim={((FarmerSprite)player.Sprite).PauseForSingleAnimation}, " +
                    $"playerControlSeq={Game1.currentLocation?.currentEvent?.playerControlSequence}");
            }
            else
            {
                Logger.LogVerbose($"MaintainRunState: restored running={shouldRun} (speed={player.Speed})");
                lastRunStateBail = null;
            }
        }

        /// <summary>Logs why MaintainRunState did nothing, but only when the reason changes, since it runs every tick.</summary>
        private static void LogRunStateBail(string? reason)
        {
            if (reason == lastRunStateBail)
                return;
            lastRunStateBail = reason;
            if (reason != null)
                Logger.LogVerbose($"MaintainRunState: skipped ({reason})");
        }

        private static string? lastRunStateBail;

        /// <summary>Ticks the Warrior Energy timer down and re-grants it whenever the player crosses the
        /// legendary sword shrine tile. Vanilla's own touch-action dispatch is unreliable inside an event,
        /// so we read the tile property ourselves the same way GameLocation does.</summary>
        private void UpdateWarriorEnergy()
        {
            // Race-only pickup: nothing to collect (or keep) while on foot or outside the race itself.
            if (!RaceRidingActive)
            {
                warriorEnergyTimer.Value = 0f;
                warriorEnergyBonus.Value = 0f;
                lastWarriorEnergyTile.Value = -Vector2.One;
                return;
            }

            if (warriorEnergyTimer.Value > 0f)
            {
                warriorEnergyTimer.Value -= Game1.currentGameTime.ElapsedGameTime.Milliseconds;
                if (warriorEnergyTimer.Value < 0f)
                    warriorEnergyTimer.Value = 0f;
            }

            Vector2 tile = Game1.player.Tile;
            if (tile == lastWarriorEnergyTile.Value)
                return;
            lastWarriorEnergyTile.Value = tile;

            string? touchAction = Game1.currentLocation?.doesTileHaveProperty((int)tile.X, (int)tile.Y, "TouchAction", "Back");
            if (touchAction != WarriorEnergyTouchAction)
                return;

            // Scaled off the ridden horse: a slower horse gets more out of the shrine.
            // RaceRidingActive above guarantees there is a mount.
            int totalSpeed = HorseHelper.GetRaceSpeedStat(Game1.player.mount);

            warriorEnergyTimer.Value = WarriorEnergyMs;
            warriorEnergyBonus.Value = HorseStats.WarriorEnergyBonus(totalSpeed);
            PlayWarriorEnergyTeleportEffect();
            Logger.LogVerbose($"Warrior Energy granted at tile {tile}: horse speed={totalSpeed}, +{warriorEnergyBonus.Value} speed for {WarriorEnergyMs}ms");
        }

        /// <summary>The vanilla return-scepter teleport flourish (sparkles, warp beam, flash, "wand" sound),
        /// minus everything that stops the player, so they keep racing straight through it.</summary>
        private static void PlayWarriorEnergyTeleportEffect()
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null)
                return;

            Farmer player = Game1.player;

            // Sparkle burst scattered around the rider.
            for (int i = 0; i < 12; i++)
            {
                location.temporarySprites.Add(new TemporaryAnimatedSprite(
                    354,
                    Game1.random.Next(25, 75),
                    6,
                    1,
                    new Vector2(
                        Game1.random.Next((int)player.position.X - 256, (int)player.position.X + 192),
                        Game1.random.Next((int)player.position.Y - 256, (int)player.position.Y + 192)),
                    flicker: false,
                    Game1.random.Next(2) == 0));
            }

            // Warp beam sweeping along the player's tile row.
            Point tile = player.TilePoint;
            int step = 0;
            for (int x = tile.X + 8; x >= tile.X - 8; x--)
            {
                location.temporarySprites.Add(new TemporaryAnimatedSprite(6, new Vector2(x, tile.Y) * 64f, Color.White, 8, flipped: false, 50f)
                {
                    layerDepth = 1f,
                    delayBeforeAnimationStart = step * 25,
                    motion = new Vector2(-0.25f, 0f)
                });
                step++;
            }

            Game1.flashAlpha = 1f;
            Game1.playSound("wand");
        }

        private void UpdateSprint()
        {
            if (sprintPhase.Value == SprintPhase.Ready)
                return;

            sprintTimer.Value -= Game1.currentGameTime.ElapsedGameTime.Milliseconds;
            if (sprintTimer.Value > 0f)
                return;

            if (sprintPhase.Value == SprintPhase.Sprinting)
            {
                sprintPhase.Value = SprintPhase.Exhausted;
                sprintTimer.Value = SprintCooldownMs;
            }
            else
            {
                sprintPhase.Value = SprintPhase.Ready;
                sprintTimer.Value = 0f;
            }
        }

        private void TryStartSprint()
        {
            if (sprintPhase.Value != SprintPhase.Ready || Game1.player.mount == null)
                return;

            var (_, totalSprint) = HorseHelper.GetRaceStats(Game1.player.mount);
            float durationMs = HorseStats.SprintDurationMs(totalSprint);
            Logger.LogVerbose($"Sprint (festival): sprint={totalSprint}, duration={durationMs}ms, speed=+{HorseStats.SprintSpeedBonus(totalSprint)}");

            sprintPhase.Value = SprintPhase.Sprinting;
            sprintTimer.Value = durationMs;
            Game1.playSound("fireball");
            TrainingManager.ProcessSprint(Game1.player.mount);
        }

        private void SuppressOtherBuffs()
        {
            suppressedBuffs.Value.Clear();
            foreach (string id in Game1.player.buffs.AppliedBuffs.Keys.ToList())
            {
                suppressedBuffs.Value.Add(Game1.player.buffs.AppliedBuffs[id]);
                Game1.player.buffs.Remove(id);
            }
        }

        private void RestoreSuppressedBuffs()
        {
            foreach (Buff buff in suppressedBuffs.Value)
                Game1.player.applyBuff(buff);
            suppressedBuffs.Value.Clear();
        }

        /// <summary>Returns the stable player-slot index sorted by UniqueMultiplayerID so every client agrees.</summary>
        private static int PastureSlotFor(Farmer farmer) =>
            System.Math.Max(0, Game1.getOnlineFarmers()
                .OrderBy(f => f.UniqueMultiplayerID)
                .ToList()
                .IndexOf(farmer));

        private static Point PastureSpawnForSlot(int slot) =>
            slot < ShuffledPenSlots.Length ? ShuffledPenSlots[slot] : new Point(ShuffledPenSlots[^1].X + (slot - ShuffledPenSlots.Length + 1) * 2, ShuffledPenSlots[^1].Y);

        private void SetLayerVisible(string layerName, bool visible)
        {
            var layer = Game1.currentLocation?.map.GetLayer(layerName);
            if (layer != null) layer.Visible = visible;
        }

        // ======================== Bus Arrival Cinematic ========================

        /// <summary>Bus body source rect on Game1.mouseCursors (matches the vanilla Desert/BusStop bus).</summary>
        private static readonly Rectangle BusBodySource = new(288, 1247, 128, 64);
        /// <summary>Milliseconds per frame of the bus door's 6-frame open/shut strip.</summary>
        private const float BusDoorFrameMs = 70f;
        /// <summary>How long the door sits fully open before the player steps off the bus.</summary>
        private const float BusDoorOpenHoldMs = 150f;

        /// <summary>How far right of the rest tile the bus starts, in pixels (≈ one screen of drive-in).</summary>
        private static int BusArrivalLeadPixels => System.Math.Max(960, Game1.viewport.Width);

        /// <summary>Starts the bus drive-in: hides the player inside the bus at the right of the screen and lets the
        /// camera track it as it drives to the rest tile. Mirrors the vanilla Desert arrival, driven from the mod
        /// because the festival runs as an event (the location's own update/draw don't apply).</summary>
        private void EnterBusArrival(Event festival)
        {
            phase.Value = Phase.Arrival;
            activeDef.Value = DefinitionForEvent(festival);
            FestivalDefinition def = activeDef.Value!;

            // BoardBusToSummerFestival/BusLeftToDesert_Prefix leave these set from the departure
            // animation at the BusStop. Vanilla's own warp completion clears them automatically,
            // but only when !eventUp, and by the time we land here the festival Event is already
            // up, so that auto-clear is skipped. Left set, the player stays frozen/uncontrollable
            // once the cinematic below hands control back.
            Game1.freezeControls = false;
            Game1.viewportFreeze = false;

            Game1.changeMusicTrack("silence", track_interruptable: true);
            Game1.displayFarmer = false;
            Game1.player.CanMove = false;
            Game1.player.Halt();

            float restX = def.BusParkTile.X * 64f;
            busArrivalPos.Value = new Vector2(restX + BusArrivalLeadPixels, def.BusParkTile.Y * 64f);
            busArrivalMotion.Value = new Vector2(-6f, 0f);
            busArrivalDoorTimer.Value = -1f;

            busDoorSprite.Value = new TemporaryAnimatedSprite("LooseSprites\\Cursors",
                new Rectangle(368, 1311, 16, 38), busArrivalPos.Value + new Vector2(16f, 26f) * 4f,
                flipped: false, 0f, Color.White)
            {
                interval = 999999f,
                animationLength = 1,
                holdLastFrame = true,
                layerDepth = 1f,
                scale = 4f,
            };

            Game1.player.Position = busDoorSprite.Value.Position;
            Game1.playSound("busDriveOff");
        }

        /// <summary>Drives the bus left toward the rest tile each tick (vanilla easing), then opens the door and
        /// hands off to the pasture phase.</summary>
        private void UpdateBusArrival(Event festival)
        {
            FestivalDefinition def = Def;
            TemporaryAnimatedSprite? door = busDoorSprite.Value;
            Game1.player.CanMove = false;
            Game1.player.freezePause = 100;

            // Parked: swing the door open, hold it, then start the pasture phase. The frame is driven
            // straight off the timer rather than by TemporaryAnimatedSprite.update: the strip runs
            // shut(5) -> open(0), and the only way to play a strip backwards is pingPong, which bounces
            // and shuts the door again the instant it finishes opening.
            if (busArrivalDoorTimer.Value >= 0f)
            {
                busArrivalDoorTimer.Value -= Game1.currentGameTime.ElapsedGameTime.Milliseconds;
                if (door != null)
                {
                    int frame = (int)System.Math.Clamp(
                        (busArrivalDoorTimer.Value - BusDoorOpenHoldMs) / BusDoorFrameMs, 0f, 5f);
                    door.sourceRect = new Rectangle(288 + frame * 16, 1311, 16, 38);
                }
                if (busArrivalDoorTimer.Value <= 0f)
                    this.FinishBusArrival(festival);
                return;
            }

            float restX = def.BusParkTile.X * 64f;
            Vector2 pos = busArrivalPos.Value;
            Vector2 motion = busArrivalMotion.Value;

            // Keep the camera tracking the bus (player rides hidden inside).
            if (door != null)
                Game1.player.Position = door.Position;

            // Ease down within 256px of the rest tile (vanilla).
            if (pos.X - restX < 256f)
                motion.X = System.Math.Min(-1f, motion.X * 0.98f);

            if (System.Math.Abs(pos.X - restX) <= System.Math.Abs(motion.X * 1.5f))
            {
                pos.X = restX;
                busArrivalPos.Value = pos;
                busArrivalMotion.Value = Vector2.Zero;
                if (door != null)
                {
                    // Hand the door over to the timer-driven open above: park it on the shut frame and
                    // stop it animating itself.
                    door.sourceRect = new Rectangle(288 + 5 * 16, 1311, 16, 38);
                    door.sourceRectStartingPos = new Vector2(288f, 1311f);
                    door.currentParentTileIndex = 0;
                    door.animationLength = 1;
                    door.interval = 999999f;
                    door.holdLastFrame = true;
                    door.Position = pos + new Vector2(16f, 26f) * 4f;
                }
                Game1.playSound("trashcanlid");
                busArrivalDoorTimer.Value = 6 * BusDoorFrameMs + BusDoorOpenHoldMs;
                return;
            }

            pos += motion;
            busArrivalPos.Value = pos;
            busArrivalMotion.Value = motion;
            if (door != null)
            {
                door.Position += motion;
                door.update(Game1.currentGameTime);
            }
        }

        /// <summary>Ends the cinematic: drops the player at the bus door and begins the pasture phase.</summary>
        private void FinishBusArrival(Event festival)
        {
            FestivalDefinition def = Def;
            this.ParkBus(Game1.currentLocation);
            busDoorSprite.Value = null;
            busArrivalDoorTimer.Value = -1f;
            busArrivalMotion.Value = Vector2.Zero;

            Game1.displayFarmer = true;
            Game1.player.Position = new Vector2(def.BusDropTile.X * 64f, def.BusDropTile.Y * 64f);
            Game1.player.faceDirection(Game1.down);
            Game1.player.freezePause = 0;

            // Re-stamp the mount window so EnterPasture still brings the horse after the cinematic delay.
            if (lastRiddenMount.Value != null)
                lastMountedTick.Value = Game1.ticks;

            this.EnterPasture(festival);
        }

        /// <summary>Draws the moving bus + door over the world during the arrival and departure cinematics.
        /// Once parked, the bus is a sprite on the location instead (see <see cref="ParkBus"/>).</summary>
        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (phase.Value != Phase.Arrival && phase.Value != Phase.Departure)
                return;

            Vector2 screen = Game1.GlobalToLocal(Game1.viewport, busArrivalPos.Value);
            e.SpriteBatch.Draw(Game1.mouseCursors, screen, BusBodySource, Color.White, 0f, Vector2.Zero, 4f,
                SpriteEffects.None, 1f);
            BusTrailerManager.DrawTrailerBehindBus(e.SpriteBatch, busArrivalPos.Value, 1f);
            busDoorSprite.Value?.draw(e.SpriteBatch);
        }

        /// <summary>
        /// Leaves the bus parked at the drop-off for the rest of the festival, the way the Desert's bus
        /// waits for you. The cinematic's mod-drawn bus can't stay: it's drawn in RenderedWorld, so it
        /// paints over the player. Instead the bus body, its open door and the hitched trailer become
        /// sprites on the festival location itself, at the vanilla Desert's layer depths, so the world
        /// sorts them and the player walks in front of the bus rather than behind it.
        /// </summary>
        private void ParkBus(GameLocation location)
        {
            Vector2 pos = busArrivalPos.Value;
            float depth = (pos.Y + 192f) / 10000f;

            var body = new TemporaryAnimatedSprite("LooseSprites\\Cursors", BusBodySource, pos,
                flipped: false, 0f, Color.White)
            {
                interval = 999999f,
                animationLength = 1,
                holdLastFrame = true,
                layerDepth = depth,
                scale = 4f,
            };
            // Frame 0 of the door strip is the open door (frame 5 is shut), the same sprite the Desert
            // leaves standing while its bus waits for you.
            var door = new TemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(288, 1311, 16, 38),
                pos + new Vector2(16f, 26f) * 4f, flipped: false, 0f, Color.White)
            {
                interval = 999999f,
                animationLength = 1,
                holdLastFrame = true,
                layerDepth = depth + 1E-05f,
                scale = 4f,
            };

            location.temporarySprites.Add(body);
            location.temporarySprites.Add(door);
            parkedBusSprites.Value.Add(body);
            parkedBusSprites.Value.Add(door);

            int trailerCount = location.temporarySprites.Count;
            BusTrailerManager.AddParkedTrailerSprite(location, pos, depth);
            if (location.temporarySprites.Count > trailerCount)
                parkedBusSprites.Value.Add(location.temporarySprites[^1]);

            busParked.Value = true;
            busExitArmed.Value = false;
        }

        /// <summary>Removes the parked bus sprites. The temp map is discarded when the festival ends, but
        /// this keeps the sprites from lingering if the location outlives the festival.</summary>
        private void UnparkBus()
        {
            if (parkedBusSprites.Value.Count > 0)
            {
                GameLocation? loc = Game1.currentLocation;
                foreach (TemporaryAnimatedSprite sprite in parkedBusSprites.Value)
                    loc?.temporarySprites.Remove(sprite);
                parkedBusSprites.Value.Clear();
            }
            busParked.Value = false;
            busExitArmed.Value = false;
        }

        /// <summary>The tiles that count as the parked bus's doorway.</summary>
        private static IEnumerable<Point> BusExitTiles(FestivalDefinition def) =>
            def.BusExitTiles ?? new[] { def.BusDropTile, new Point(def.BusDropTile.X, def.BusDropTile.Y - 1) };

        /// <summary>
        /// Walking into the parked bus's door is how you leave the festival. Vanilla's touch actions are
        /// suppressed during an event, so this polls the player's tile instead, and only fires once they
        /// have stepped off the doorway (otherwise the drop tile would prompt the moment they arrive).
        /// </summary>
        private void CheckBusDoorExit()
        {
            if (!busParked.Value || Game1.activeClickableMenu != null || RaceFestival == null)
                return;

            Point tile = Game1.player.TilePoint;
            if (!BusExitTiles(Def).Contains(tile))
            {
                busExitArmed.Value = true;
                return;
            }
            if (!busExitArmed.Value)
                return;

            busExitArmed.Value = false;
            this.PromptLeaveOnBus();
        }

        /// <summary>Asks whether to board the bus home, then ends the festival. Mirrors vanilla's
        /// leave-the-festival prompt: a confirmation in single player, and a ready check in multiplayer
        /// so the whole party rides back together.</summary>
        private void PromptLeaveOnBus()
        {
            Game1.player.Halt();

            void Board(Farmer _)
            {
                Game1.exitActiveMenu();
                this.StartBusDeparture();
            }

            if (!Game1.IsMultiplayer)
            {
                Game1.activeClickableMenu = new ConfirmationDialog(
                    "Take the bus back home?", Board);
                return;
            }

            Game1.netReady.SetLocalReady("festivalEnd", ready: true);
            Game1.activeClickableMenu = new ReadyCheckDialog("festivalEnd", allowCancel: true, Board,
                (_) =>
                {
                    Game1.netReady.SetLocalReady("festivalEnd", ready: false);
                    Game1.exitActiveMenu();
                });
        }

        /// <summary>
        /// Everyone agreed to go home: the reverse of the arrival. The parked bus goes back to being
        /// mod-drawn (so it can move again), the door shuts with the player hidden inside, and the bus
        /// pulls away west while the camera holds still, exactly like the vanilla Desert's departure.
        /// </summary>
        private void StartBusDeparture()
        {
            Vector2 pos = busArrivalPos.Value;
            this.UnparkBus();
            phase.Value = Phase.Departure;

            // Everyone boards: the rider dismounts and every horse they brought is loaded back into the
            // trailer, so nothing is left standing on the festival map as the bus pulls away.
            this.ReleaseFestivalMount();
            this.StowBusHorses();

            Game1.player.CanMove = false;
            Game1.player.Halt();
            Game1.displayFarmer = false;
            Game1.changeMusicTrack("silence", track_interruptable: true);

            // Frames 0 -> 5 is the door swinging shut.
            busDoorSprite.Value = new TemporaryAnimatedSprite("LooseSprites\\Cursors",
                new Rectangle(288, 1311, 16, 38), pos + new Vector2(16f, 26f) * 4f,
                flipped: false, 0f, Color.White)
            {
                interval = 70f,
                animationLength = 6,
                holdLastFrame = true,
                layerDepth = 1f,
                scale = 4f,
            };
            busArrivalMotion.Value = Vector2.Zero;
            busArrivalDoorTimer.Value = 6 * 70f + 200f;
            Game1.playSound("trashcanlid");

            // The camera stays with the stop while the bus drives out of frame, like the Desert's.
            Game1.viewportFreeze = true;
        }

        /// <summary>Shuts the door, then accelerates the bus off the west edge of the screen and ends the
        /// festival once it's out of sight.</summary>
        private void UpdateBusDeparture(Event festival)
        {
            TemporaryAnimatedSprite? door = busDoorSprite.Value;
            Game1.player.CanMove = false;
            Game1.player.freezePause = 100;
            door?.update(Game1.currentGameTime);

            // Ride along with the door. Game1.displayFarmer is ignored while a festival event is drawing
            // the location's farmers, so without this the player is left standing at the stop, visibly
            // watching their own bus leave. The camera is frozen, so they simply travel out of frame.
            Game1.displayFarmer = false;
            if (door != null)
                Game1.player.Position = door.Position;

            // Door still closing.
            if (busArrivalDoorTimer.Value > 0f)
            {
                busArrivalDoorTimer.Value -= Game1.currentGameTime.ElapsedGameTime.Milliseconds;
                if (busArrivalDoorTimer.Value <= 0f)
                    Game1.playSound("busDriveOff");
                return;
            }

            // Vanilla's departure acceleration.
            Vector2 motion = busArrivalMotion.Value;
            motion.X -= 0.075f;
            busArrivalMotion.Value = motion;
            busArrivalPos.Value += motion;
            if (door != null)
                door.Position += motion;

            // Fully out of frame (the bus body is 512px wide, plus whatever the trailer adds behind it),
            // so end the festival and ride home.
            if (busArrivalPos.Value.X + 512f + BusTrailerManager.TrailerTailPixels < Game1.viewport.X)
            {
                busDoorSprite.Value = null;
                busArrivalMotion.Value = Vector2.Zero;
                Game1.viewportFreeze = false;
                this.EndFestival();
            }
        }

        // ======================== End Bus Arrival Cinematic ========================

        /// <summary>
        /// Safeguard against the festival-entry black screen. The festival set-up script fades the screen
        /// to black (leaving <see cref="Game1.fadeToBlackAlpha"/> at 1) and only clears it with
        /// <c>globalFadeToClear</c> as its final command. If a stray menu/dialogue opens on this client
        /// mid-setup, the event stops advancing before that clear runs, stranding the player on a fully
        /// black screen with only the cursor until they click the menu away. Runs while the set-up event
        /// is mid-script (before <c>playerControl</c>); if the screen stays fully black for longer than a
        /// normal near-instant set-up would take, we clear the fade ourselves.
        /// </summary>
        private void GuardEntryFade()
        {
            // Only a near-opaque screen counts as stuck; a fade still in progress is left to finish.
            if (Game1.fadeToBlackAlpha < 0.99f && !Game1.globalFade)
            {
                entryBlackTicks.Value = 0;
                return;
            }

            // ~1.5s at 60fps. A normal set-up reaches playerControl within a tick or two, so this only
            // trips when the event is genuinely stalled behind a blocking menu.
            if (++entryBlackTicks.Value < 90)
                return;

            // Log the blocking menu (the likeliest culprit) so a recurrence can be traced to its type.
            string blocker = Game1.activeClickableMenu != null
                ? Game1.activeClickableMenu.GetType().Name
                : (Game1.dialogueUp ? "dialogue box" : "none (no menu open)");
            Logger.LogVerbose($"Festival entry fade appears stuck black; force-clearing the screen fade. Blocking menu: {blocker}");
            Game1.globalFade = false;
            Game1.fadeToBlack = false;
            Game1.fadeToBlackAlpha = 0f;
            entryBlackTicks.Value = 0;
        }

        /// <summary>The vanilla ready-check id shown as the "waiting for other players" dialog on festival entry.</summary>
        private const string FestivalStartReadyCheck = "festivalStart";

        /// <summary>
        /// Rescues a player stranded on the vanilla <c>festivalStart</c> "waiting for other players" dialog.
        /// That ready check requires every online farmer to be ready before anyone warps in, so a straggler
        /// who reaches the festival after others have already entered waits forever: the players inside no
        /// longer report themselves ready, so the count can never complete. When we detect another player is
        /// already inside the festival (or after a long absolute timeout), we confirm the dialog locally,
        /// which runs its original warp callback and takes this player in. Festivals are per-client temp-map
        /// events, so letting one straggler through independently is safe.
        /// </summary>
        private void GuardStuckFestivalReadyCheck()
        {
            if (Game1.activeClickableMenu is not ReadyCheckDialog rcd || rcd.checkName != FestivalStartReadyCheck)
            {
                festivalReadyStuckTicks.Value = 0;
                return;
            }

            FestivalDefinition? def = CurrentWindowFestival();
            if (def == null)
            {
                festivalReadyStuckTicks.Value = 0;
                return;
            }

            festivalReadyStuckTicks.Value++;

            // Primary trigger: someone is already inside the festival, so the check can never complete.
            // A brief grace (~2s) avoids racing a normal simultaneous entry that's about to confirm anyway.
            bool someoneInside = Game1.getOnlineFarmers()
                .Any(f => f.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID
                    && f.currentLocation?.Name == def.LocationName);
            bool stuckWithPeerInside = someoneInside && festivalReadyStuckTicks.Value >= 120;

            // Absolute fallback (~30s) in case the peer-location check misses (e.g. a disconnecting player).
            bool absoluteTimeout = festivalReadyStuckTicks.Value >= 1800;

            if (!stuckWithPeerInside && !absoluteTimeout)
                return;

            Logger.LogVerbose($"festivalStart ready check stuck for {festivalReadyStuckTicks.Value} ticks "
                + $"(peerInside={someoneInside}); confirming locally to enter {def.LocationName}.");
            festivalReadyStuckTicks.Value = 0;
            rcd.confirm(); // runs the dialog's captured warp callback → warps this player into the festival
        }

        private void EnterPasture(Event festival)
        {
            phase.Value = Phase.Pasture;
            activeDef.Value = DefinitionForEvent(festival);
            competitor.Value = null;
            FestivalDefinition def = activeDef.Value!;

            var penSlotsSeed = new System.Random((int)(Game1.uniqueIDForThisGame ^ (uint)Game1.Date.TotalDays) + 3);
            var slots = (Point[])def.PenSlots.Clone();
            for (int i = slots.Length - 1; i > 0; i--)
            {
                int j = penSlotsSeed.Next(i + 1);
                (slots[i], slots[j]) = (slots[j], slots[i]);
            }
            shuffledPenSlots.Value = slots;
            Game1.changeMusicTrack(def.PastureMusic, track_interruptable: false, MusicContext.Event);

            // Hide phase-specific layers regardless of what the TMX file saved.
            this.SetLayerVisible("Racing", false);
            this.SetLayerVisible("AwardsEvent", false);

            // Cache NPC placement data from the phase layers.
            setupSpectators = this.ReadNpcPlacements("Set-Up", sveOnly: true);
            racingSpectators = this.ReadNpcPlacements("Racing");
            ceremonySpectators = this.ReadNpcPlacements("AwardsEvent");

            // showWorldCharacters must be set on every client so the event renderer draws horses placed by any client.
            festival.showWorldCharacters = true;

            // Every client builds its own stalls, since the festival temp map's objects aren't net-synced.
            this.SpawnStartingStalls();
            if (def.PenHorseTile.HasValue) this.SpawnPenHorse();
            this.SpawnPenNpcHorses();
            this.SpawnDecorativeHorses();
            this.SpawnSpectators(setupSpectators);
            this.SpawnShopNpcs();
            this.SpawnBookie();
            this.SpawnDesertTrader();
            // After every Set-Up actor exists, including the game's loadActors ones, so nobody is
            // left wearing a work outfit or an off-season variant.
            this.ApplyFestivalAppearances();
            this.SyncBack2WaterTiles(Game1.currentLocation);


            // Bus festival: the horses picked in the HorseBusLoadMenu ride along instead of the mount.
            if (def.BusArrival && SummerBusSelectionMade)
            {
                this.PlaceBusSelectionInPasture();
                return;
            }

            Horse? horse = lastRiddenMount.Value;
            bool arrivedMounted = horse != null && (Game1.ticks - lastMountedTick.Value) <= EntryMountWindowTicks;
            if (!arrivedMounted || horse == null)
                return;

            int slot = PastureSlotFor(Game1.player);
            competitor.Value = horse;

            PlaceHorseInPasture(horse, slot);
            this.Helper.Multiplayer.SendMessage(
                new PastureHorseMessage(horse.HorseId.ToString(), slot),
                MsgPastureHorse,
                modIDs: new[] { this.Helper.ModRegistry.ModID });

            Logger.LogVerbose($"Brought '{horse.Name}' into the festival pasture (slot {slot}).");
        }

        /// <summary>Unloads the horses chosen at the Bus Stop into the pasture. Stable horses with a live
        /// Horse entity are moved (net-synced, announced via MsgPastureHorse); barn-only horses get a
        /// temporary Horse built from their FarmAnimal data (announced via MsgBusHorse). The first horse
        /// becomes the competitor; mounting the other switches it (see <see cref="UpdatePasture"/>).</summary>
        private void PlaceBusSelectionInPasture()
        {
            int slot = PastureSlotFor(Game1.player);
            // Extra horses use slots past the per-farmer and NPC pen-horse ranges so every client
            // resolves them to the same unoccupied spots.
            int extraSlotBase = Game1.getOnlineFarmers().Count() + Def.NpcRiderNames.Length;
            int index = 0;

            foreach (long animalId in SummerBusHorseIds)
            {
                if (index >= BusHorseCapacity) break;
                FarmAnimal? animal = HorseHelper.GetAllBarnHorses().FirstOrDefault(a => a.myID.Value == animalId);
                if (animal == null) continue;

                // Extras interleave by farmer count so every player's extra horses land on distinct slots.
                int horseSlot = index == 0 ? slot : extraSlotBase + (index - 1) * Game1.getOnlineFarmers().Count() + slot;
                Horse? horse = FindStableHorseForAnimal(animalId);
                if (horse != null)
                {
                    PlaceHorseInPasture(horse, horseSlot);
                    this.Helper.Multiplayer.SendMessage(
                        new PastureHorseMessage(horse.HorseId.ToString(), horseSlot),
                        MsgPastureHorse,
                        modIDs: new[] { this.Helper.ModRegistry.ModID });
                }
                else
                {
                    horse = CreateBusHorse(animal);
                    PlaceHorseInPasture(horse, horseSlot);
                    this.Helper.Multiplayer.SendMessage(
                        new BusHorseMessage(
                            horse.HorseId.ToString(),
                            horse.Name,
                            horse.modData[HorseHelper.HorseSkinKey],
                            horse.modData[HorseHelper.OverlaysKey],
                            horseSlot),
                        MsgBusHorse,
                        modIDs: new[] { this.Helper.ModRegistry.ModID });
                }

                busFestivalHorses.Value.Add(horse);
                if (competitor.Value == null)
                    competitor.Value = horse;
                Logger.LogVerbose($"Unloaded '{animal.Name}' from the bus into pasture slot {horseSlot}.");
                index++;
            }
        }

        /// <summary>Builds a temporary festival Horse from a barn horse's FarmAnimal data. Race stats ride
        /// along via the borrowed-stat modData keys, which GetRaceStats falls back to when the horse has
        /// no stable backing.</summary>
        private static Horse CreateBusHorse(FarmAnimal animal)
        {
            var horse = new Horse(System.Guid.NewGuid(), 0, 0);
            horse.Name = animal.Name;
            horse.displayName = animal.displayName;
            horse.modData[HorseHelper.HorseSkinKey] = HorseTexturePatches.SkinNameFromId(animal.skinID.Value);
            horse.modData[HorseHelper.OverlaysKey] = HorseHelper.GetOverlaysForAnimal(animal);
            var stats = animal.GetHorseStats();
            horse.modData[HorseHelper.BorrowedSpeedKey] = stats.TotalSpeed.ToString();
            horse.modData[HorseHelper.BorrowedSprintKey] = stats.TotalSprint.ToString();
            horse.modData[HorseHelper.BorrowedJumpKey] = stats.TotalJump.ToString();
            return horse;
        }

        private void OnMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            // Bus claims apply everywhere (the receiver may still be standing at the Bus Stop).
            if (e.Type == MsgBusClaim)
            {
                var claimMsg = e.ReadAs<BusClaimMessage>();
                foreach (long animalId in claimMsg.AnimalIds)
                {
                    if (claimMsg.Release)
                    {
                        if (BusHorseClaims.TryGetValue(animalId, out long owner) && owner == claimMsg.PlayerId)
                            BusHorseClaims.Remove(animalId);
                    }
                    else
                    {
                        BusHorseClaims[animalId] = claimMsg.PlayerId;
                    }
                }
                Logger.LogVerbose($"Bus claim update from player {claimMsg.PlayerId}: {(claimMsg.Release ? "released" : "claimed")} {claimMsg.AnimalIds.Count} horse(s).");
                return;
            }

            if (e.Type == MsgSpringWin)
            {
                this.HandleSpringWinNews(e.ReadAs<SpringWinMessage>().WinnerName, isWinner: false);
                return;
            }

            if (e.Type == MsgOpenReadyCheck)
            {
                string readyCheckId = e.ReadAs<string>();
                if (RaceFestival != null && phase.Value == Phase.Pasture)
                    this.OpenRaceReadyCheck(readyCheckId);
                else
                    pendingRaceReadyCheckId.Value = readyCheckId;
                return;
            }


            if (e.Type == MsgPlayerFinished && IsHost && RaceFestival != null)
            {
                this.RecordFinish(e.ReadAs<long>());
                return;
            }

            if (e.Type == MsgPlayerDisqualified && IsHost && RaceFestival != null)
            {
                this.RecordDisqualification(e.ReadAs<long>());
                return;
            }

            if (e.Type == MsgStartCeremony && RaceFestival != null)
            {
                var ceremony = e.ReadAs<StartCeremonyMessage>();
                this.StartCeremony(ceremony.RankedPlayerIds, ceremony.EndBarrierId);
                return;
            }

            if (e.Type == MsgBorrowedHorse && RaceFestival != null)
            {
                var bmsg = e.ReadAs<BorrowedHorseMessage>();
                if (!System.Guid.TryParse(bmsg.HorseId, out System.Guid bid))
                    return;
                var borrowedHorse = new Horse(bid, 0, 0);
                borrowedHorse.Name = "MarniesLoan_remote";
                borrowedHorse.modData[HorseHelper.HorseSkinKey] = bmsg.Skin;
                borrowedHorse.modData[HorseHelper.OverlaysKey] = "Saddle,Bridle";
                RaceFestival.showWorldCharacters = true;
                PlaceHorseInPasture(borrowedHorse, bmsg.Slot);
                Logger.LogVerbose($"Placed remote borrowed horse in pasture slot {bmsg.Slot}.");
                return;
            }

            if (e.Type == MsgBusHorse && RaceFestival != null)
            {
                var busMsg = e.ReadAs<BusHorseMessage>();
                if (!System.Guid.TryParse(busMsg.HorseId, out System.Guid busHorseId))
                    return;
                var busHorse = new Horse(busHorseId, 0, 0);
                busHorse.Name = busMsg.Name;
                busHorse.modData[HorseHelper.HorseSkinKey] = busMsg.Skin;
                busHorse.modData[HorseHelper.OverlaysKey] = busMsg.Overlays;
                RaceFestival.showWorldCharacters = true;
                PlaceHorseInPasture(busHorse, busMsg.Slot);
                busFestivalHorses.Value.Add(busHorse);
                Logger.LogVerbose($"Placed remote bus horse '{busMsg.Name}' in pasture slot {busMsg.Slot}.");
                return;
            }

            if (e.Type == MsgNpcSprint && RaceFestival != null && !IsHost)
            {
                var sprintMsg = e.ReadAs<NpcSprintMessage>();
                if (sprintMsg.NpcIndex >= 0 && sprintMsg.NpcIndex < npcRacers.Count)
                {
                    NpcRacer r = npcRacers[sprintMsg.NpcIndex];
                    r.NpcSprintPhase = SprintPhase.Sprinting;
                    r.NpcSprintTimer = sprintMsg.DurationMs;
                }
                return;
            }

            if (e.Type != MsgPastureHorse || RaceFestival == null)
                return;

            var msg = e.ReadAs<PastureHorseMessage>();
            if (!System.Guid.TryParse(msg.HorseId, out System.Guid id))
                return;

            Horse? horse = Utility.getAllCharacters().OfType<Horse>()
                .FirstOrDefault(h => h.HorseId == id);
            if (horse == null)
            {
                this.Monitor.Log($"Received PastureHorse message but couldn't find horse {msg.HorseId}.", LogLevel.Warn);
                return;
            }

            RaceFestival.showWorldCharacters = true;
            PlaceHorseInPasture(horse, msg.Slot);
            Logger.LogVerbose($"Placed remote horse '{horse.Name}' in pasture slot {msg.Slot}.");
        }

        /// <summary>Creates a borrowed horse and sets competitor/borrowedFestivalHorse. No pasture
        /// placement or broadcast; call AssignBorrowedHorse for the pasture-phase UI flow.</summary>
        private void EnsureCompetitorHorse()
        {
            if (competitor.Value != null) return;

            string skin = AllSkins[Game1.random.Next(AllSkins.Length)];
            string name = MarnieHorseNames[Game1.random.Next(MarnieHorseNames.Length)];
            var horse = new Horse(System.Guid.NewGuid(), 0, 0);
            horse.Name = name;
            horse.modData[HorseHelper.HorseSkinKey] = skin;
            horse.modData[HorseHelper.OverlaysKey] = "Saddle,Bridle";
            horse.modData[HorseHelper.BorrowedSpeedKey] = "10";
            horse.modData[HorseHelper.BorrowedSprintKey] = "10";
            horse.modData[HorseHelper.BorrowedJumpKey] = "10";
            borrowedFestivalHorse.Value = horse;
            competitor.Value = horse;
            Logger.LogVerbose($"Auto-assigned borrowed horse '{horse.Name}' to {Game1.player.Name}.");
        }

        /// <summary>Brings a late joiner's claim table up to date; without this a farmhand connecting
        /// after another player boarded could load already-taken horses onto a second bus.</summary>
        private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
        {
            if (!IsHost || BusHorseClaims.Count == 0)
                return;

            foreach (var claimsByPlayer in BusHorseClaims.GroupBy(kv => kv.Value))
            {
                this.Helper.Multiplayer.SendMessage(
                    new BusClaimMessage(claimsByPlayer.Select(kv => kv.Key).ToList(), claimsByPlayer.Key, Release: false),
                    MsgBusClaim,
                    modIDs: new[] { this.Helper.ModRegistry.ModID },
                    playerIDs: new[] { e.Peer.PlayerID });
            }
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            BusHorseClaims.Clear();
            SummerBusHorseIds.Clear();
            SummerBusSelectionMade = false;
            SummerFareWaived = false;

            this.DeliverFestivalNotices();
        }

        /// <summary>
        /// Drops each festival's advance-notice letter into the local player's mailbox on the morning
        /// AnnouncementDaysBefore days ahead of it: Lewis for the walk-in races, the Horse Racing
        /// Committee for the summer away race. Runs per-client (DayStarted fires on every machine), so
        /// every online player gets their own copy; the letter repeats every year, so it's added straight
        /// to the mailbox rather than gated on mailReceived.
        /// </summary>
        private void DeliverFestivalNotices()
        {
            foreach (FestivalDefinition def in Festivals)
            {
                int noticeDay = def.Day - def.AnnouncementDaysBefore;
                if (def.AnnouncementMailId == null
                    || noticeDay < 1
                    || Game1.currentSeason != def.Season
                    || Game1.dayOfMonth != noticeDay
                    || Game1.player.mailbox.Contains(def.AnnouncementMailId))
                    continue;

                // Away races: only invite players who could actually make the trip.
                if (def.AnnouncementRequiresBusAccess
                    && (!Game1.MasterPlayer.mailReceived.Contains("ccVault") || !BusTrailerManager.IsBuilt))
                    continue;

                Game1.player.mailbox.Add(def.AnnouncementMailId);
                Logger.LogVerbose($"Queued festival notice {def.AnnouncementMailId} for {Game1.player.Name}.");
            }
        }

        /// <summary>
        /// Shows the start-of-festival heads-up for "away" festivals that aren't registered in
        /// Data/Festivals/FestivalDates (and so get no vanilla "The X Festival is starting" message). Fires
        /// once, when the clock reaches the festival's StartTime on its day, only for players who can actually
        /// attend (bus repaired + trailer built). TimeChanged fires per-client on the synced clock, so every
        /// player sees it locally without any broadcast.
        /// </summary>
        private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            foreach (FestivalDefinition def in Festivals)
            {
                if (def.HeadsUpMessage == null
                    || Game1.currentSeason != def.Season
                    || Game1.dayOfMonth != def.Day
                    || e.NewTime != def.StartTime)
                    continue;

                if (!Game1.MasterPlayer.mailReceived.Contains("ccVault") || !BusTrailerManager.IsBuilt)
                    return;

                Game1.showGlobalMessage(def.HeadsUpMessage);
                return;
            }
        }

        private void AssignBorrowedHorse()
        {
            if (Game1.currentLocation == null) return;

            this.EnsureCompetitorHorse();
            Horse horse = competitor.Value!;

            int slot = PastureSlotFor(Game1.player);
            PlaceHorseInPasture(horse, slot);

            this.Helper.Multiplayer.SendMessage(
                new BorrowedHorseMessage(horse.HorseId.ToString(), horse.modData[HorseHelper.HorseSkinKey], slot),
                MsgBorrowedHorse,
                modIDs: new[] { this.Helper.ModRegistry.ModID });

            Logger.LogVerbose($"Placed borrowed horse '{horse.Name}' in pasture slot {slot}.");
        }

        private static void PlaceHorseInPasture(Horse horse, int slot)
        {
            GameLocation loc = Game1.currentLocation;
            horse.currentLocation?.characters.Remove(horse);
            horse.rider = null;
            horse.dismounting.Value = false;
            horse.mounting.Value = false;
            horse.EventActor = false;
            horse.controller = null;
            horse.currentLocation = loc;
            horse.Position = TileToPixels(PastureSpawnForSlot(slot));
            horse.Halt();
            horse.faceDirection(Game1.right);
            if (!loc.characters.Contains(horse))
                loc.characters.Add(horse);
            HorseAnimations.SetGrazing(horse);
        }

        private void UpdatePasture()
        {
            this.CheckBusDoorExit();
            this.RefreshMarketPastureHorses();

            if (pendingRaceReadyCheckId.Value != null && RaceFestival != null && !readyCheckOpen.Value)
            {
                string id = pendingRaceReadyCheckId.Value;
                pendingRaceReadyCheckId.Value = null;
                this.OpenRaceReadyCheck(id);
            }

            // With two bus horses in the pasture, whichever one the player mounts races.
            if (Game1.player.mount is Horse mounted && mounted != competitor.Value
                && busFestivalHorses.Value.Contains(mounted))
                competitor.Value = mounted;

            Horse? horse = competitor.Value;
            if (horse == null)
                return;
            if (horse.Sprite?.CurrentAnimation == null)
                HorseAnimations.SetGrazing(horse);
        }

        // Copies water-tile status from Back2 into waterTiles so that water placed on
        // Back2 (with a passable sand tile on Back) still animates correctly.
        private void SyncBack2WaterTiles(GameLocation location)
        {
            if (location?.map == null) return;
            var back2 = location.map.GetLayer("Back2");
            if (back2 == null) return;

            int w = back2.LayerWidth;
            int h = back2.LayerHeight;

            location.waterTiles ??= new StardewValley.WaterTiles(w, h);

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    var tile = back2.Tiles[x, y];
                    if (tile == null) continue;

                    tile.TileIndexProperties.TryGetValue("Water", out var v);
                    if (v == null) tile.Properties.TryGetValue("Water", out v);

                    if (v?.ToString() == "T" &&
                        x < location.waterTiles.waterTiles.GetLength(0) &&
                        y < location.waterTiles.waterTiles.GetLength(1))
                    {
                        location.waterTiles.waterTiles[x, y].isWater = true;
                        location.waterTiles.waterTiles[x, y].isVisible = true;
                    }
                }
            }
        }

        private void SpawnPenHorse()
        {
            Point tile = Def.PenHorseTile!.Value;
            GameLocation loc = Game1.currentLocation;
            var horse = new Horse(System.Guid.NewGuid(), tile.X, tile.Y);
            horse.Name = "PenHorse";
            horse.modData[HorseHelper.HorseSkinKey] = AllSkins[Game1.random.Next(AllSkins.Length)];
            horse.modData[HorseHelper.OverlaysKey] = "Saddle,Bridle";
            horse.currentLocation = loc;
            horse.Position = TileToPixels(tile);
            horse.Halt();
            horse.faceDirection(Game1.left);
            horse.EventActor = true;
            if (!loc.characters.Contains(horse))
                loc.characters.Add(horse);
            HorseAnimations.SetGrazing(horse);
            penHorse.Value = horse;
            this.LockJasOnHorse(loc);
        }

        private void SpawnPenNpcHorses()
        {
            GameLocation loc = Game1.currentLocation;
            int playerSlotCount = Game1.getOnlineFarmers().Count();
            var rng = new System.Random((int)(Game1.uniqueIDForThisGame ^ (uint)Game1.Date.TotalDays));

            for (int i = 0; i < Def.NpcRiderNames.Length; i++)
            {
                Point tile = PastureSpawnForSlot(playerSlotCount + i);
                var horse = new Horse(System.Guid.NewGuid(), tile.X, tile.Y);
                horse.Name = Def.NpcRiderNames[i] + "PenHorse";
                horse.modData[HorseHelper.HorseSkinKey] = AllSkins[rng.Next(AllSkins.Length)];
                string saddleId = Def.NpcRiderNames[i] switch
                {
                    "Abigail" => "HorseTycoon.SaddleLavender",
                    "Sebastian" => "HorseTycoon.SaddleRed",
                    _ => "HorseTycoon.SaddleBrown",
                };
                HorseHelper.EquipSaddle(horse, saddleId);
                horse.currentLocation = loc;
                horse.Position = TileToPixels(tile);
                horse.Halt();
                horse.faceDirection(HorseFacingPool[rng.Next(HorseFacingPool.Length)]);
                horse.EventActor = true;
                if (!loc.characters.Contains(horse))
                    loc.characters.Add(horse);
                HorseAnimations.SetGrazing(horse);
                penNpcHorses.Add(horse);
            }
        }

        private void DespawnPenNpcHorses()
        {
            var loc = Game1.currentLocation;
            foreach (Horse h in penNpcHorses)
                loc?.characters.Remove(h);
            penNpcHorses.Clear();
        }

        /// <summary>Fills the paddock beside the market stalls. At festivals with shops the horses
        /// shown are the ones actually on offer today (the Horse Seller's sale list first, then the
        /// Stud Shop's stallions) so their skins match the shop menus; any offers past the last
        /// paddock tile just aren't shown. They're untacked, and a horse leaves the paddock once its
        /// offer is no longer available to this player (see <see cref="RefreshMarketPastureHorses"/>).
        /// Festivals without shops fall back to random-skin background horses.</summary>
        private void SpawnDecorativeHorses()
        {
            GameLocation loc = Game1.currentLocation;
            var rng = new System.Random((int)(Game1.uniqueIDForThisGame ^ (uint)Game1.Date.TotalDays) + 1);
            List<HorseOffer> offers = Def.HorseSellerTile != null || Def.StudShopTile != null
                ? HorseMarket.GetSaleOffers().Concat(HorseMarket.GetStudOffers()).Where(o => o.IsAvailable).ToList()
                : new List<HorseOffer>();

            for (int i = 0; i < Def.PastureBgSlots.Length; i++)
            {
                Point tile = Def.PastureBgSlots[i];
                HorseOffer? offer = i < offers.Count ? offers[i] : null;
                var horse = new Horse(System.Guid.NewGuid(), tile.X, tile.Y);
                horse.Name = "DecorativeHorse";
                // HorseOffer uses "" for the base skin, which is "Roan" as a horse skin id.
                horse.modData[HorseHelper.HorseSkinKey] = offer != null
                    ? (string.IsNullOrEmpty(offer.SkinId) ? "Roan" : offer.SkinId)
                    : AllSkins[rng.Next(AllSkins.Length)];
                if (offer != null)
                    marketPastureHorses[horse] = offer;
                horse.currentLocation = loc;
                horse.Position = TileToPixels(tile);
                horse.Halt();
                horse.faceDirection(HorseFacingPool[rng.Next(HorseFacingPool.Length)]);
                horse.EventActor = true;
                if (!loc.characters.Contains(horse))
                    loc.characters.Add(horse);
                HorseAnimations.SetGrazing(horse);
                decorativeHorses.Add(horse);
            }
        }

        private void DespawnDecorativeHorses()
        {
            var loc = Game1.currentLocation;
            foreach (Horse h in decorativeHorses)
                loc?.characters.Remove(h);
            decorativeHorses.Clear();
            marketPastureHorses.Clear();
        }

        /// <summary>Removes a paddock horse once the local player can no longer buy/hire it (sold to
        /// anyone for sale offers, hired by this player for studs), so the paddock matches the menus.</summary>
        private void RefreshMarketPastureHorses()
        {
            if (marketPastureHorses.Count == 0)
                return;

            foreach (var pair in marketPastureHorses.Where(p => !p.Value.IsAvailable).ToList())
            {
                Game1.currentLocation?.characters.Remove(pair.Key);
                decorativeHorses.Remove(pair.Key);
                marketPastureHorses.Remove(pair.Key);
                Logger.LogVerbose($"Removed market paddock horse '{pair.Value.Name}' (offer no longer available).");
            }
        }

        private void LockJasOnHorse(GameLocation loc)
        {
            NPC? jas = loc.characters.OfType<NPC>().FirstOrDefault(c => c.Name == "Jas");
            if (jas == null)
                return;
            jas.EventActor = true;
            jas.faceDirection(Game1.left);
            jas.Sprite.currentFrame = 23;
            jas.Sprite.StopAnimation();
            jasOnHorse.Value = jas;
        }

        private void AdvanceHorseAnimations()
        {
            GameTime time = Game1.currentGameTime;

            // Re-arm before advancing: animateOnce can only step an animation that already exists, so a
            // horse whose CurrentAnimation is null is skipped by the loops below and stays frozen on one
            // frame for the entire race. Vanilla Horse.update normally assigns the gallop frames, but it
            // does not run for a client-local borrowed horse, so the local mount needs it done here.
            this.EnsureLocalMountAnimation();

            // A mounted horse is reachable both through location.characters and through Farmer.mount;
            // dedupe so it advances once per tick rather than at double speed.
            HashSet<AnimatedSprite> advanced = new();
            foreach (NPC npc in Game1.currentLocation.characters)
                if (npc is Horse h && h.Sprite?.CurrentAnimation != null && advanced.Add(h.Sprite))
                    h.Sprite.animateOnce(time);
            foreach (Farmer farmer in Game1.getOnlineFarmers())
                if (farmer.mount?.Sprite?.CurrentAnimation != null && advanced.Add(farmer.mount.Sprite))
                    farmer.mount.Sprite.animateOnce(time);
        }

        /// <summary>
        /// Keeps the local player's mount on the gallop animation matching its facing direction while
        /// racing, mirroring what <see cref="UpdateNpcRacers"/> does for NPC racers. This is deliberately
        /// a no-op whenever the sprite already holds the right animation, so it does not fight vanilla
        /// Horse.update on the clients where that is working; it only fills in the gap, which makes a
        /// missing animation self-correcting on the next tick instead of permanent.
        /// </summary>
        private void EnsureLocalMountAnimation()
        {
            Farmer? player = Game1.player;
            Horse? mount = player?.mount;
            if (player == null || mount?.Sprite == null)
                return;

            if (player.isMoving() && IsInAnyRacingPhase)
            {
                int dir = mount.FacingDirection;
                if (dir != localMountAnimDir.Value || !IsGalloppingAnimation(mount, dir))
                {
                    localMountAnimDir.Value = dir;
                    SetGalloppingAnimation(mount, dir);
                }
                return;
            }

            // Standing still (or not racing yet): leave whatever animation is already playing alone,
            // but never leave the sprite with none at all.
            if (mount.Sprite.CurrentAnimation == null)
            {
                localMountAnimDir.Value = StoppedAnimDir;
                HorseAnimations.SetIdle(mount);
            }
        }

        /// <summary>Redraw our buff icons during the race, since the buff HUD is suppressed during events.</summary>
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (showBettingMoneyBox.Value)
                Game1.dayTimeMoneyBox.draw(e.SpriteBatch);

            int slot = 0;

            if (RaceRidingActive && sprintPhase.Value != SprintPhase.Ready)
            {
                bool sprinting = sprintPhase.Value == SprintPhase.Sprinting;
                // Vanilla buff sheet indices: Speed = 9, Exhausted = 25.
                this.DrawFestivalBuff(e.SpriteBatch, slot++, sprintTimer.Value,
                    sprinting ? "Horse Sprint" : "Horse Exhausted",
                    sprinting ? 9 : 25,
                    sprinting ? sprintBuffIcon : null);
            }

            if (warriorEnergyTimer.Value > 0f)
            {
                // Vanilla buff sheet index 21 = Yoba's Blessing (the sheet is indexed by Buff id).
                this.DrawFestivalBuff(e.SpriteBatch, slot++, warriorEnergyTimer.Value,
                    $"Warrior Energy (+{warriorEnergyBonus.Value:0.#} speed)", 21, null);
            }
        }

        /// <summary>Draws one festival buff icon with its remaining time, stacked down the top-right corner.</summary>
        private void DrawFestivalBuff(SpriteBatch b, int slot, float msLeft, string label, int sheetIndex, Texture2D? icon)
        {
            const int iconSize = 64;
            int x = Game1.uiViewport.Width - iconSize - 24;
            int y = 24 + slot * (iconSize + 12);
            int secondsLeft = (int)System.Math.Ceiling(msLeft / 1000f);
            string timeText = (secondsLeft / 60) + ":" + (secondsLeft % 60).ToString("00");

            if (icon != null)
            {
                b.Draw(icon, new Rectangle(x, y, iconSize, iconSize), Color.White);
            }
            else
            {
                Rectangle src = Game1.getSourceRectForStandardTileSheet(Game1.buffsIcons, sheetIndex, 16, 16);
                b.Draw(Game1.buffsIcons, new Vector2(x, y), src, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            }
            Utility.drawTextWithShadow(b, timeText, Game1.smallFont,
                new Vector2(x + (iconSize - Game1.smallFont.MeasureString(timeText).X) / 2f, y + iconSize - 8f), Color.White);

            if (new Rectangle(x, y, iconSize, iconSize).Contains(Game1.getOldMouseX(), Game1.getOldMouseY()))
                IClickableMenu.drawHoverText(b, label + "\n" + timeText + " left", Game1.smallFont);
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            // During the start countdown swallow action/tool presses. Without this, Fence.checkForAction
            // detects the fully-enclosed rider as "trapped" and smashes an adjacent fence to free them.
            if (this.startCountdown.Value >= 0f)
            {
                if (e.Button.IsActionButton() || e.Button.IsUseToolButton() || e.Button == SButton.LeftShift || e.Button == SButton.RightShift)
                    this.Helper.Input.Suppress(e.Button);
                return;
            }

            if (RaceRidingActive && (e.Button == SButton.LeftShift || e.Button == SButton.RightShift))
            {
                this.TryStartSprint();
                return;
            }

            if (phase.Value != Phase.Pasture || readyCheckOpen.Value)
                return;

            if (Game1.activeClickableMenu != null)
                return;
            if (!e.Button.IsActionButton())
                return;

            Event? festival = RaceFestival;
            NPC? pam = FacedPam(festival);
            NPC? starter = RaceStarter;
            NPC? horseSeller = festival?.getActorByName(HorseSellerActorName);
            NPC? studKeeper = festival?.getActorByName(StudShopActorName);
            NPC? itemKeeper = festival?.getActorByName(ItemShopActorName);
            NPC? bookie = festival?.getActorByName(BookieActorName);

            bool nearPam = pam != null;
            bool nearStarter = starter != null && IsPlayerFacing(starter);
            bool nearSeller = horseSeller != null && IsPlayerFacingKeeper(horseSeller);
            bool nearStud = studKeeper != null && IsPlayerFacingKeeper(studKeeper);
            bool nearItemShop = itemKeeper != null && IsPlayerFacingKeeper(itemKeeper);
            bool nearBookie = bookie != null && IsPlayerFacingKeeper(bookie);
            bool nearTrader = this.IsFacingDesertTrader();

            if (!nearPam && !nearStarter && !nearSeller && !nearStud && !nearItemShop && !nearBookie && !nearTrader)
                return;

            this.Helper.Input.Suppress(e.Button);

            // The desert merchant's caravan, parked at the away festival.
            if (nearTrader)
            {
                this.OpenDesertTraderShop();
                return;
            }

            // The Bouncer runs the odds book at away festivals.
            if (nearBookie)
            {
                this.ShowBookieDialog(bookie!);
                return;
            }

            // Festival market stalls (summer away festival).
            if (nearSeller)
            {
                this.OpenHorseSellerShop(horseSeller!);
                return;
            }
            if (nearStud)
            {
                this.OpenStudShop(studKeeper!);
                return;
            }
            if (nearItemShop)
            {
                this.OpenItemShop(itemKeeper!);
                return;
            }

            // Pam handles betting. She always answers with the book; the button press is suppressed
            // above, so she never falls through to her ordinary festival/default dialogue.
            if (nearPam)
            {
                if (pamGreeted.Value)
                {
                    // One bet per festival, so there's nothing left to offer, but still her line, not vanilla's.
                    Game1.drawObjectDialogue("Your bet's already down, hon. Go on and enjoy the race.");
                    return;
                }

                showBettingMoneyBox.Value = true;
                Response[] betResponses =
                {
                    new("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                    new("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
                };
                Game1.currentLocation.createQuestionDialogue(
                    "Hey, wanna put some money on the race? I'm runnin' the book. Winner takes double.",
                    betResponses,
                    (_, betAnswer) =>
                    {
                        if (betAnswer != "Yes")
                        {
                            // Player declined, so hide the money box; they can re-approach Pam to be asked again.
                            Game1.afterDialogues = () => { showBettingMoneyBox.Value = false; };
                            return;
                        }

                        Response[] racerOptions = BuildBetRacerResponses();
                        if (racerOptions.Length == 0)
                        {
                            pamGreeted.Value = true;
                            return;
                        }

                        Game1.afterDialogues = () =>
                            Game1.currentLocation.createQuestionDialogue(
                                 "Alright, who's gonna place the best? Can't bet on yourself.",
                                racerOptions,
                                (_, racerAnswer) =>
                                {
                                    RecordBet(racerAnswer);

                                    var amountOptions = new List<Response>();
                                    foreach (int betChoice in Def.BetAmounts)
                                    {
                                        // High-stakes bets (1000g+) only open up from year 2 onward.
                                        if (betChoice >= 1000 && Game1.year < 2)
                                            continue;
                                        amountOptions.Add(new Response(betChoice.ToString(), $"{betChoice}g"));
                                    }
                                    amountOptions.Add(new Response("nevermind", "Nevermind"));

                                    Game1.afterDialogues = () =>
                                        Game1.currentLocation.createQuestionDialogue(
                                            "How much you puttin' in?",
                                            amountOptions.ToArray(),
                                            (_, amountAnswer) =>
                                            {
                                                if (amountAnswer == "nevermind")
                                                {
                                                    betTargetFarmerId.Value = null;
                                                    betTargetNpcName.Value = null;
                                                    Game1.afterDialogues = () => { showBettingMoneyBox.Value = false; };
                                                    return;
                                                }
                                                if (int.TryParse(amountAnswer, out int amount) && Game1.player.Money >= amount)
                                                {
                                                    betAmount.Value = amount;
                                                    Game1.player.Money -= amount;
                                                    Game1.playSound("purchase");
                                                    Game1.dayTimeMoneyBox.moneyShakeTimer = 800;
                                                    pamGreeted.Value = true;
                                                    Game1.afterDialogues = () =>
                                                    {
                                                        Game1.drawObjectDialogue("You're all set! Good luck out there.");
                                                        Game1.afterDialogues = () => { showBettingMoneyBox.Value = false; };
                                                    };
                                                }
                                                else
                                                {
                                                    betTargetFarmerId.Value = null;
                                                    betTargetNpcName.Value = null;
                                                    pamGreeted.Value = true;
                                                    Game1.afterDialogues = () =>
                                                    {
                                                        Game1.drawObjectDialogue("Ha! You ain't got that kind of money, hon.");
                                                        Game1.afterDialogues = () => { showBettingMoneyBox.Value = false; };
                                                    };
                                                }
                                            }, pam);
                                }, pam);
                    }, pam);
                return;
            }

            // The festival's host (Lewis at home, Sandy in the desert) handles the race start.
            if (nearStarter)
                this.ShowStarterRaceDialog();
        }

        private Response[] BuildBetRacerResponses()
        {
            var responses = new List<Response>();

            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                if (farmer.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                    continue;
                responses.Add(new Response($"farmer_{farmer.UniqueMultiplayerID}", farmer.Name));
            }

            int playerCount = System.Math.Max(1, Game1.getOnlineFarmers().Count());
            int npcSlots = System.Math.Min(Def.NpcRiderNames.Length, System.Math.Max(0, MaxRacers - playerCount));
            for (int i = 0; i < npcSlots; i++)
                responses.Add(new Response($"npc_{Def.NpcRiderNames[i]}", Def.NpcRiderNames[i]));

            return responses.ToArray();
        }

        private void RecordBet(string answer)
        {
            if (answer.StartsWith("farmer_") && long.TryParse(answer.Substring(7), out long farmerId))
                betTargetFarmerId.Value = farmerId;
            else if (answer.StartsWith("npc_"))
                betTargetNpcName.Value = answer.Substring(4);
        }

        /// <summary>The festival's host: the NPC who starts the race, announces it, and runs the awards.
        /// A plain Set-Up actor (Lewis at the valley's own races, Sandy at the desert one), so this is
        /// null until the festival event has loaded its actors.</summary>
        private NPC? RaceStarter => RaceFestival?.getActorByName(Def.StarterName);

        /// <summary>
        /// The host's race-start conversation. When the festival gives them small talk
        /// (<see cref="FestivalDefinition.StarterGreeting"/>) the first approach plays that first and
        /// only then asks the question; later approaches go straight to the question, so a player who
        /// says "no" and comes back isn't made to sit through the chat again.
        /// </summary>
        private void ShowStarterRaceDialog()
        {
            NPC? starter = RaceStarter;
            string? greeting = Def.StarterGreeting;

            if (greeting != null && !starterGreeted.Value)
            {
                starterGreeted.Value = true;
                if (starter != null)
                {
                    starter.CurrentDialogue.Push(new Dialogue(starter, null, greeting));
                    Game1.drawDialogue(starter);
                }
                else
                    Game1.drawObjectDialogue(greeting);

                Game1.afterDialogues = this.AskStarterToBegin;
                return;
            }

            this.AskStarterToBegin();
        }

        /// <summary>The host's actual "ready to race?" question (or the equivalent for a player who
        /// turned up without a horse, or the please-wait line for a farmhand).</summary>
        private void AskStarterToBegin()
        {
            NPC? starter = RaceStarter;
            Response[] yesNo =
            {
                new("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                new("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
            };

            if (competitor.Value == null)
            {
                if (IsHost)
                {
                    Game1.currentLocation.createQuestionDialogue(
                        Def.StarterNoHorseQuestionHost,
                        yesNo,
                        (_, answer) =>
                        {
                            showBettingMoneyBox.Value = false;
                            if (answer != "Yes") return;
                            this.AssignBorrowedHorse();
                            Game1.drawObjectDialogue($"Great! {competitor.Value!.Name} here will treat you well!");
                            Game1.afterDialogues = this.BeginRace;
                        }, starter);
                }
                else
                {
                    Game1.currentLocation.createQuestionDialogue(
                        Def.StarterNoHorseQuestion,
                        yesNo,
                        (_, answer) =>
                        {
                            showBettingMoneyBox.Value = false;
                            if (answer != "Yes") return;
                            this.AssignBorrowedHorse();
                            Game1.drawObjectDialogue($"Great! {competitor.Value!.Name} here will treat you well!");
                        }, starter);
                }
                return;
            }

            if (!IsHost)
            {
                showBettingMoneyBox.Value = false;
                Game1.drawObjectDialogue(Def.StarterWaitingForHostLine);
                return;
            }

            Game1.currentLocation.createQuestionDialogue(Def.StarterReadyQuestion, yesNo,
                (_, answer) =>
                {
                    showBettingMoneyBox.Value = false;
                    if (answer == "Yes")
                        this.BeginRace();
                }, starter);
        }

        private static bool IsHost =>
            !Game1.IsMultiplayer || Game1.serverHost == null || Game1.player.Equals(Game1.serverHost.Value);

        /// <summary>
        /// The Pam the player is currently facing, or null. She can reach the festival two ways: as an
        /// event actor the map's Set-Up layer spawned, or as a plain world character the event draws
        /// because <c>showWorldCharacters</c> is on. Only the actor lookup used to be checked, so at a
        /// festival whose map doesn't place her, the world Pam was never recognized and her ordinary
        /// dialogue answered instead of the betting book.
        /// </summary>
        private static NPC? FacedPam(Event? festival)
        {
            NPC? actor = festival?.getActorByName("Pam");
            if (actor != null && IsPlayerFacing(actor))
                return actor;

            NPC? worldPam = Game1.currentLocation?.getCharacterFromName("Pam");
            return worldPam != null && IsPlayerFacing(worldPam) ? worldPam : null;
        }

        /// <summary>Is the player facing a stall keeper, either directly or across the counter they stand
        /// behind? Market stalls on the festival map draw a solid counter row in front of the keeper, so the
        /// customer stands two tiles away and <see cref="IsPlayerFacing"/>'s one-tile grab area never reaches
        /// them. Keepers on open ground (e.g. the bookie) still match on the direct check.</summary>
        private static bool IsPlayerFacingKeeper(NPC npc)
        {
            if (IsPlayerFacing(npc))
                return true;

            Point step = Game1.player.FacingDirection switch
            {
                Game1.up => new Point(0, -1),
                Game1.right => new Point(1, 0),
                Game1.down => new Point(0, 1),
                Game1.left => new Point(-1, 0),
                _ => Point.Zero,
            };
            if (step == Point.Zero)
                return false;

            Vector2 playerTile = Game1.player.Tile;
            var overCounter = new Vector2(playerTile.X + step.X * 2, playerTile.Y + step.Y * 2);
            return npc.Tile == overCounter;
        }

        // Mirrors vanilla's grab-area check: is the player facing this NPC and within reach?
        private static bool IsPlayerFacing(NPC npc)
        {
            Rectangle playerBounds = Game1.player.GetBoundingBox();
            const int Reach = 64;
            Rectangle grabArea = Game1.player.FacingDirection switch
            {
                Game1.up => new Rectangle(playerBounds.X, playerBounds.Y - Reach, playerBounds.Width, Reach),
                Game1.right => new Rectangle(playerBounds.Right, playerBounds.Y, Reach, playerBounds.Height),
                Game1.down => new Rectangle(playerBounds.X, playerBounds.Bottom, playerBounds.Width, Reach),
                Game1.left => new Rectangle(playerBounds.X - Reach, playerBounds.Y, Reach, playerBounds.Height),
                _ => Rectangle.Empty,
            };
            return npc.GetBoundingBox().Intersects(grabArea);
        }

        private void BeginRace()
        {
            if (!Game1.IsMultiplayer)
            {
                this.FadeToRace();
                return;
            }

            // Fresh id per attempt so the check starts un-ready on every client (see ReadyCheckPrefix).
            string id = $"{ReadyCheckPrefix}.{Game1.uniqueIDForThisGame}.{Game1.ticks}";
            this.OpenRaceReadyCheck(id);
            this.Helper.Multiplayer.SendMessage(id, MsgOpenReadyCheck,
                modIDs: new[] { this.Helper.ModRegistry.ModID });
        }

        /// <summary>Opens the race ready check on this screen. ReadyCheckDialog's update() loop marks the local
        /// player ready each tick and fires onConfirm once all online players have reached it. The host passes a
        /// unique <paramref name="id"/> per race attempt (broadcast to farmhands) so every client shares a fresh
        /// netReady check that starts un-ready (see <see cref="ReadyCheckPrefix"/>).
        ///
        /// This mirrors the vanilla Egg Festival's <c>waitForOtherPlayers startContest</c> barrier: after the host
        /// confirms with the host, every client hits a non-cancelable ready check that auto-readies on arrival and
        /// holds until all clients are present, then the race begins simultaneously. <c>allowCancel: false</c>
        /// matches that barrier: once the host says go it's a committed sync point, so no client can flash past
        /// or back out and desync the start.</summary>
        private void OpenRaceReadyCheck(string id)
        {
            if (readyCheckOpen.Value || phase.Value != Phase.Pasture)
                return;

            readyCheckOpen.Value = true;
            Game1.activeClickableMenu = new ReadyCheckDialog(
                id,
                allowCancel: false,
                onConfirm: (_) =>
                {
                    Game1.exitActiveMenu();
                    readyCheckOpen.Value = false;
                    // The go barrier reuses this start barrier's (unique) id so all clients agree on it.
                    goBarrierId.Value = id + ".go";
                    this.FadeToRace();
                });
        }

        private void LineUp()
        {
            // Never mint the competitor horse directly here. EnsureCompetitorHorse only constructs the
            // object; a horse created at this point would skip PlaceHorseInPasture (so its sprite would
            // have no CurrentAnimation and stay frozen on one frame for the whole race) and skip the
            // MsgBorrowedHorse broadcast (so no other client would have a proxy for it). Route through
            // the full assignment path instead, which places, animates, and announces it.
            if (competitor.Value == null)
            {
                Logger.LogVerbose("No competitor horse at lineup, running borrowed-horse assignment now.");
                this.AssignBorrowedHorse();
            }

            Horse? horse = competitor.Value;
            GameLocation loc = Game1.currentLocation;
            if (horse == null || loc == null)
                return;

            // Lineup (not Racing yet): riders are placed in stalls and the host announces; the countdown, music,
            // and NPC movement wait until every client clears his dialogue and the go barrier releases.
            phase.Value = Phase.Lineup;
            goBarrierOpened.Value = false;

            this.SetLayerVisible("Racing", true);
            this.DespawnSpectators();
            this.SpawnSpectators(racingSpectators);
            this.RemoveDesertTrader();

            this.SuppressOtherBuffs();

            int slot = PastureSlotFor(Game1.player);
            Point stall = StallHorseTile(slot);

            if (pastureAnimal.Value != null)
            {
                loc.animals.Remove(pastureAnimal.Value.myID.Value);
                pastureAnimal.Value = null;
            }

            horse.rider = null;
            horse.dismounting.Value = false;
            horse.mounting.Value = false;
            horse.controller = null;
            horse.currentLocation?.characters.Remove(horse);
            horse.currentLocation = loc;
            horse.Halt();
            horse.Position = TileToPixels(stall);
            if (!loc.characters.Contains(horse))
                loc.characters.Add(horse);

            Game1.player.Halt();
            Game1.player.completelyStopAnimatingOrDoingAction();
            Game1.player.Position = TileToPixels(stall);
            Game1.player.faceDirection(Game1.right);

            // Mount directly without the NetMutex, since the async round-trip can silently fail during festival
            // events. Setting rider + mounting.Value = true is enough; Horse.update finalizes the mount state.
            horse.rider = Game1.player;
            horse.mounting.Value = true;
            Game1.player.synchronizedJump(6f);
            Game1.player.Halt();
            Game1.player.UsingTool = false;

            this.DespawnPenNpcHorses();
            this.SpawnNpcRacers(loc, System.Math.Max(1, Game1.getOnlineFarmers().Count()));

            // Silence the pasture music for the host's announcement; the start cue/race music begin with the
            // countdown once everyone has clicked through (see StartRaceCountdown).
            Game1.changeMusicTrack("none", track_interruptable: false, MusicContext.Event);
        }

        /// <summary>Transitions from the pasture to the starting stalls with a slow fade: fade to black, reposition
        /// riders into their stalls while the screen is dark (<see cref="LineUp"/>), then fade back in and let
        /// the host give their announcement. Runs per client; the later go barrier re-syncs everyone before the start.</summary>
        private void FadeToRace()
        {
            // Enter Lineup up front so the rider is held (CanMove off) through both fades, before repositioning.
            phase.Value = Phase.Lineup;
            goBarrierOpened.Value = false;
            announcementShown.Value = false;

            Game1.globalFadeToBlack(() =>
            {
                this.LineUp();
                Game1.globalFadeToClear(this.AnnounceRace, RaceFadeSpeed);
            }, RaceFadeSpeed);
        }

        /// <summary>The host's pre-race announcement, shown on each client once its rider is in the stall. When the
        /// player clicks through it, <see cref="OpenCountdownBarrier"/> holds them until every client has done the
        /// same, then the countdown begins, mirroring the Egg Festival's cutscene-then-waitForOtherPlayers.</summary>
        private void AnnounceRace()
        {
            // Page breaks must use the delimited form "#$b#": Dialogue.parseDialogueString splits on '#'
            // first, so a bare "$b" stays embedded in the text and never becomes a break. A page with no
            // emotion token shows the host's neutral portrait; "$h" switches them to happy.
            string announcement = Def.RaceAnnouncement;

            announcementShown.Value = true;

            NPC? starter = RaceStarter;
            if (starter != null)
            {
                starter.CurrentDialogue.Push(new Dialogue(starter, null, announcement));
                Game1.drawDialogue(starter);
            }
            else
            {
                Game1.drawObjectDialogue(announcement);
            }
            Game1.afterDialogues = this.OpenCountdownBarrier;
        }

        /// <summary>After the host's announcement, hold this client on a non-cancelable barrier until every player
        /// has clicked through, then start the countdown together. In single player it starts immediately.</summary>
        private void OpenCountdownBarrier()
        {
            // Idempotent: afterDialogues and the UpdateLineup fallback can both reach here; only the first acts.
            if (goBarrierOpened.Value)
                return;
            goBarrierOpened.Value = true;

            if (!Game1.IsMultiplayer || goBarrierId.Value == null)
            {
                this.StartRaceCountdown();
                return;
            }

            string id = goBarrierId.Value;
            Game1.activeClickableMenu = new ReadyCheckDialog(
                id,
                allowCancel: false,
                onConfirm: (_) =>
                {
                    Game1.exitActiveMenu();
                    this.StartRaceCountdown();
                });
        }

        /// <summary>Begins the Racing phase: the start cue plays, the countdown ticks (race music kicks in near
        /// its end), and gates open when it completes. Runs simultaneously on all clients via the go barrier.</summary>
        private void StartRaceCountdown()
        {
            phase.Value = Phase.Racing;
            goBarrierId.Value = null;
            Game1.player.freezePause = 5000;
            Game1.playSound(RaceStartSoundCue);
            this.startCountdown.Value = RaceStartSoundMs;
            this.raceMusicStarted.Value = false;
        }

        /// <summary>Holds riders in their stalls during the host's announcement and the go barrier. No NPC movement
        /// or finish checks run here, so the race can't advance until <see cref="StartRaceCountdown"/> fires.</summary>
        private void UpdateLineup()
        {
            Game1.player.CanMove = false;

            // Fallback: if the announcement dialogue never armed afterDialogues (e.g. it was dismissed by some
            // other menu closing), open the go barrier once we're no longer showing a menu so we can't stall.
            // Gated on announcementShown and no active fade so it can't fire during the fade-in before the
            // dialogue even appears.
            if (announcementShown.Value && !goBarrierOpened.Value
                && !Game1.globalFade && Game1.activeClickableMenu == null && !Game1.dialogueUp)
                this.OpenCountdownBarrier();
        }

        // Slot 0 = center, odd slots go below (+), even slots above (-), alternating outward.
        private static int StallYOffset(int slot) =>
            slot == 0 ? 0 : slot % 2 == 1 ? ((slot + 1) / 2) * 2 : -(slot / 2) * 2;

        private static Point StallHorseTile(int slot) => new(Def.StartStall.X, Def.StartStall.Y + StallYOffset(slot));

        /// <summary>Build a fenced stall per online player with a gate on the east wall.
        /// The stall fully encloses the rider; they're held by CanMove and input is suppressed until release
        /// so Fence.checkForAction's "free the trapped farmer" logic never fires.</summary>
        private void SpawnStartingStalls()
        {
            GameLocation loc = Game1.currentLocation;
            if (stallsSpawned.Value || loc == null)
                return;

            int count = DebugAllStalls
                ? MaxRacers
                : System.Math.Max(1, Game1.getOnlineFarmers().Count()) + Def.NpcRiderNames.Length;

            int minYOffset = 0, maxYOffset = 0;
            for (int i = 0; i < count; i++)
            {
                int yo = StallYOffset(i);
                if (yo < minYOffset) minYOffset = yo;
                if (yo > maxYOffset) maxYOffset = yo;
            }
            int topY = Def.StartStall.Y + minYOffset - 1;
            int bottomY = Def.StartStall.Y + maxYOffset + 1;

            for (int y = topY; y <= bottomY; y++)
                this.AddStallFence(loc, new Vector2(Def.StartStall.X - 1, y), isGate: false);

            // The gate at (X+1, hy) must be flanked above and below by fence so its drawSum == 1500
            // (a vertical line). Without those flanks, Fence.updateWhenCurrentLocation auto-closes it every tick.
            for (int i = 0; i < count; i++)
            {
                int hy = StallHorseTile(i).Y;
                this.AddStallFence(loc, new Vector2(Def.StartStall.X, hy - 1), isGate: false);
                this.AddStallFence(loc, new Vector2(Def.StartStall.X, hy + 1), isGate: false);
                this.AddStallFence(loc, new Vector2(Def.StartStall.X + 1, hy - 1), isGate: false);
                this.AddStallFence(loc, new Vector2(Def.StartStall.X + 1, hy + 1), isGate: false);
                this.AddStallFence(loc, new Vector2(Def.StartStall.X + 1, hy), isGate: true);
            }

            stallsSpawned.Value = true;
        }

        /// <summary>Place a fence or gate, skipping already-occupied tiles. Gates use <see cref="RaceStartGate"/>
        /// so an open gate has no collision footprint inside the festival.</summary>
        private void AddStallFence(GameLocation loc, Vector2 tile, bool isGate)
        {
            if (loc.objects.ContainsKey(tile))
                return;
            Fence fence = isGate ? new RaceStartGate(tile, Def.StallFenceId) : new Fence(tile, Def.StallFenceId, isGate: false);
            if (isGate)
                fence.gatePosition.Value = 0;
            loc.objects[tile] = fence;
            stallFenceTiles.Value.Add(tile);
        }

        /// <summary>CanMove is forced off every tick because Horse.update re-enables it as the mount finalizes.</summary>
        private void UpdateStartCountdown()
        {
            if (this.startCountdown.Value < 0f)
                return;

            if (this.startCountdown.Value > 0f)
            {
                Game1.player.CanMove = false;
                this.startCountdown.Value -= Game1.currentGameTime.ElapsedGameTime.Milliseconds;
                if (!this.raceMusicStarted.Value && this.startCountdown.Value <= 1000f)
                {
                    this.raceMusicStarted.Value = true;
                    Game1.changeMusicTrack(Def.RaceMusic, track_interruptable: false, MusicContext.Event);
                }
                if (this.startCountdown.Value > 0f)
                    return;
            }

            this.startCountdown.Value = -1f;
            Game1.player.CanMove = true;
            raceStartTime.Value = Game1.currentGameTime.TotalGameTime;
            int totalPlayers = System.Math.Max(1, Game1.getOnlineFarmers().Count());
            int totalSlots = totalPlayers + Def.NpcRiderNames.Length;
            for (int i = 0; i < totalSlots; i++)
                this.OpenStartGate(i);
        }

        /// <summary>The flank fences give the gate drawSum 1500 so Fence.updateWhenCurrentLocation won't
        /// auto-close it. Once open, <see cref="RaceStartGate"/> has no collision so the rider passes through.</summary>
        private void OpenStartGate(int slot)
        {
            GameLocation loc = Game1.currentLocation;
            if (loc == null)
                return;
            Vector2 gateTile = new(Def.StartStall.X + 1, StallHorseTile(slot).Y);
            if (loc.objects.TryGetValue(gateTile, out var obj) && obj is Fence { isGate.Value: true } gate)
            {
                gate.toggleGate(true, false, Game1.player);
                gate.gatePosition.Value = 88;
            }
        }

        private void RemoveStartingStalls()
        {
            GameLocation loc = Game1.currentLocation;
            if (loc != null)
                foreach (Vector2 tile in stallFenceTiles.Value)
                    if (loc.objects.TryGetValue(tile, out var obj) && obj is Fence)
                        loc.objects.Remove(tile);
            stallFenceTiles.Value.Clear();
            stallsSpawned.Value = false;
        }

        private void CheckFinish()
        {
            Vector2 t = Game1.player.Tile;
            bool inBand = t.X >= Def.FinishMin.X && t.X <= Def.FinishMax.X
                       && t.Y >= Def.FinishMin.Y && t.Y <= Def.FinishMax.Y;
            if (!inBand)
                return;

            phase.Value = Phase.Finished;
            Game1.addHUDMessage(new HUDMessage("Finished!", HUDMessage.achievement_type));
            var elapsed = Game1.currentGameTime.TotalGameTime - raceStartTime.Value;
            Logger.LogVerbose($"Race time for {Game1.player.Name}: {(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}");

            if (IsHost)
                this.RecordFinish(Game1.player.UniqueMultiplayerID);
            else
                this.Helper.Multiplayer.SendMessage(
                    Game1.player.UniqueMultiplayerID,
                    MsgPlayerFinished,
                    modIDs: new[] { this.Helper.ModRegistry.ModID });
        }

        private void CheckDisqualification()
        {
            if (disqualified.Value || startCountdown.Value >= 0f)
                return;

            Vector2 t = Game1.player.Tile;
            bool offEast = Def.DqZoneEastOfX >= 0 && t.X > Def.DqZoneEastOfX && t.Y < Def.DqZoneNorthOfY;
            bool offWest = Def.DqZoneWestOfX >= 0 && t.X < Def.DqZoneWestOfX;
            if (!offEast && !offWest)
                return;

            disqualified.Value = true;
            phase.Value = Phase.Finished;
            Game1.player.CanMove = false;

            // Teleport player and horse to just past the finish line so they wait in the spectator area.
            var arrivalOffset = new Vector2(0f, 64f);
            Game1.player.Position = TileToPixels(Def.DqArrivalTile) + arrivalOffset;
            Horse? dqHorse = competitor.Value;
            if (dqHorse != null)
            {
                dqHorse.Position = TileToPixels(Def.DqArrivalTile) + arrivalOffset;
                dqHorse.Halt();
            }

            NPC? starter = RaceStarter;
            starter?.doEmote(12);
            if (starter != null)
            {
                starter.CurrentDialogue.Clear();
                starter.CurrentDialogue.Push(new Dialogue(starter, "HorseTycoon.DQ", Def.StarterDqLine));
                Game1.drawDialogue(starter);
            }
            else
                Game1.drawObjectDialogue($"{Def.StarterName}: {StripDialogueTokens(Def.StarterDqLine)}");


            if (IsHost)
                this.RecordDisqualification(Game1.player.UniqueMultiplayerID);
            else
                this.Helper.Multiplayer.SendMessage(
                    Game1.player.UniqueMultiplayerID,
                    MsgPlayerDisqualified,
                    modIDs: new[] { this.Helper.ModRegistry.ModID });
        }

        private void RecordDisqualification(long farmerId)
        {
            DisqualifiedFarmers.Add(farmerId);
            Logger.LogVerbose($"Farmer {farmerId} disqualified, recording as last-place finish.");
            this.RecordFinish(farmerId);
        }

        private void RecordFinish(long farmerId)
        {
            if (FinishOrder.Contains(farmerId))
                return;
            FinishOrder.Add(farmerId);
            Logger.LogVerbose($"Finish order recorded: position {FinishOrder.Count} = farmer {farmerId}");

            // Count the NPC racers actually spawned, not the roster size: with 5+ players some
            // NPC slots are dropped (MaxRacers cap) and those racers can never record a finish.
            int totalRacers = Game1.getOnlineFarmers().Count() + npcRacers.Count;
            if (FinishOrder.Count >= totalRacers && HostCeremonyCountdown < 0f)
                HostCeremonyCountdown = CeremonyDelayMs;
        }

        private void BroadcastCeremony()
        {
            // DQ'd players always place last; sort them behind everyone who finished legitimately.
            var orderedIds = FinishOrder
                .OrderBy(id => DisqualifiedFarmers.Contains(id) ? 1 : 0)
                .ToList();
            // Fresh id per ceremony so the end barrier starts un-ready on every client (netReady caches
            // completed checks by id until the day-start reset; see CeremonyEndCheckPrefix).
            string endBarrierId = $"{CeremonyEndCheckPrefix}.{Game1.uniqueIDForThisGame}.{Game1.ticks}";
            this.Helper.Multiplayer.SendMessage(
                new StartCeremonyMessage(orderedIds, endBarrierId),
                MsgStartCeremony,
                modIDs: new[] { this.Helper.ModRegistry.ModID });
            // Host handles it locally, since SendMessage does not deliver to the sender.
            this.StartCeremony(orderedIds, endBarrierId);
        }

        private void StartCeremony(List<long> rankedPlayerIds, string endBarrierId)
        {
            phase.Value = Phase.Ceremony;
            Game1.changeMusicTrack(Def.PastureMusic, track_interruptable: false, MusicContext.Event);
            ceremonyOrder.Value = new List<long>(rankedPlayerIds);
            ceremonyStep.Value = 0;
            ceremonyEndBarrierId.Value = endBarrierId;
            ceremonyEnded.Value = false;

            long localId = Game1.player.UniqueMultiplayerID;
            int placement = rankedPlayerIds.IndexOf(localId); // 0-based; -1 if not in list

            var ceremonyOffset = new Microsoft.Xna.Framework.Vector2(0f, 64f);

            // Move the local rider + horse to their podium or spectator tile.
            // Spectator slot = placement index minus podium count, so it's consistent with the NPC loop below.
            if (placement >= 0 && placement < Def.WinnersCircleTiles.Length)
            {
                Point podium = Def.WinnersCircleTiles[placement];
                Game1.player.Position = TileToPixels(podium) + ceremonyOffset;

                Horse? horse = competitor.Value;
                if (horse != null)
                    horse.Position = TileToPixels(podium) + ceremonyOffset;
                FaceForCeremony(Game1.player, horse, CeremonyFacing);
            }
            else if (placement >= 0)
            {
                int spectatorSlot = placement - Def.WinnersCircleTiles.Length;
                Point spec = Def.SpectatorTiles[System.Math.Min(spectatorSlot, Def.SpectatorTiles.Length - 1)];
                Game1.player.Position = TileToPixels(spec) + ceremonyOffset;

                Horse? horse = competitor.Value;
                if (horse != null)
                    horse.Position = TileToPixels(spec) + ceremonyOffset;
                FaceForCeremony(Game1.player, horse, CeremonyFacing);
            }

            // Move NPC racers to their podium or spectator tiles.
            // Each NPC's spectator slot is its ranked index minus the podium count, the same formula
            // the player uses above, so humans and NPCs never land on the same tile.
            for (int i = 0; i < rankedPlayerIds.Count; i++)
            {
                long id = rankedPlayerIds[i];
                if (id >= 0) continue; // human player, handled above
                NpcRacer? racer = npcRacers.Find(r => r.FakeId == id);
                if (racer == null) continue;
                racer.Horse.controller = null;
                racer.Horse.Halt();
                if (i < Def.WinnersCircleTiles.Length)
                {
                    Point podium = Def.WinnersCircleTiles[i];
                    racer.Horse.Position = TileToPixels(podium) + ceremonyOffset;
                }
                else
                {
                    int spectatorSlot = i - Def.WinnersCircleTiles.Length;
                    Point spec = Def.SpectatorTiles[System.Math.Min(spectatorSlot, Def.SpectatorTiles.Length - 1)];
                    racer.Horse.Position = TileToPixels(spec) + ceremonyOffset;
                }
                FaceForCeremony(null, racer.Horse, CeremonyFacing);
                if (racer.Rider != null)
                {
                    SyncRiderToHorse(racer.Rider, racer.Horse);
                    racer.Rider.Sprite?.StopAnimation();
                    racer.Rider.faceDirection(CeremonyFacing);
                }
            }

            Game1.player.CanMove = false;
            Game1.player.Halt();

            // Freeze camera on the center of the winner's circle.
            Point center = Def.WinnersCircleTiles[System.Math.Min(1, Def.WinnersCircleTiles.Length - 1)];
            Game1.viewportFreeze = true;
            Game1.viewport.X = System.Math.Max(0, center.X * 64 - Game1.viewport.Width / 2);
            Game1.viewport.Y = System.Math.Max(0, center.Y * 64 - Game1.viewport.Height / 2);

            // Move the host to the announcer tile.
            NPC? starter = RaceStarter;
            if (starter != null)
            {
                starter.Position = TileToPixels(Def.StarterAnnouncerTile);
                starter.faceDirection(Game1.down);
            }

            this.SetLayerVisible("Racing", false);
            this.SetLayerVisible("AwardsEvent", true);
            this.DespawnSpectators();
            this.SpawnSpectators(ceremonySpectators);

            Logger.LogVerbose($"Ceremony started. Local placement (0-based): {placement}");
        }

        private bool prizeMenuWasOpen = false;
        private int suppressFadeTicks = 0;
        /// <summary>How long (in ticks) to keep cancelling screen fades after the prize menu closes.</summary>
        private const int FadeSuppressionTicks = 20;

        /// <summary>Ceremony step after the last of the host's lines: the all-players-done barrier.</summary>
        private const int CeremonyEndStep = 10;

        private void UpdateCeremony()
        {
            bool prizeMenuOpen = Game1.activeClickableMenu is ItemGrabMenu;

            // CeremonyPrizeMenu keeps the festival event from advancing when the prize menu closes, which is
            // what used to start a fade here. Backstop that for a few ticks anyway: a fade can be kicked off
            // a tick or two after the menu closes, and clearing only on the transition tick left a half-run
            // fade (fadeIn still set) to animate back in. Never runs once the ceremony has ended, so the
            // deliberate fade home is untouched.
            if (prizeMenuWasOpen && !prizeMenuOpen)
                suppressFadeTicks = FadeSuppressionTicks;
            prizeMenuWasOpen = prizeMenuOpen;

            if (suppressFadeTicks > 0)
            {
                suppressFadeTicks--;
                if (!ceremonyEnded.Value)
                {
                    Game1.fadeToBlack = false;
                    Game1.fadeIn = false;
                    Game1.globalFade = false;
                    Game1.fadeToBlackAlpha = 0f;
                }
            }

            // Wait for any open dialogue/menu to be dismissed before advancing.
            if (Game1.activeClickableMenu != null)
                return;

            // Past the last announcement the ceremony parks on the end barrier rather than stepping on.
            if (ceremonyStep.Value >= CeremonyEndStep)
            {
                this.WaitForCeremonyEnd();
                return;
            }
            this.AdvanceCeremonyStep();
        }

        /// <summary>Formats one of the host's ceremony lines, prefixed with their name. These are drawn as
        /// plain object dialogue (no portrait) because the host is standing across the winner's circle from
        /// the camera, so any dialogue tokens in the text are stripped rather than parsed.</summary>
        private string CeremonyLine(string line, string? arg = null)
        {
            string text = arg == null ? line : string.Format(line, arg);
            return $"{Def.StarterName}: {StripDialogueTokens(text)}";
        }

        /// <summary>Drops the Dialogue control codes (page breaks, portrait/mood tokens) from a line that's
        /// about to be shown as plain object dialogue, which renders them literally.</summary>
        private static string StripDialogueTokens(string text)
        {
            text = text.Replace("#$b#", " ").Replace("#$e#", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\$[a-zA-Z0-9]", "");
            return text.Trim();
        }

        private void AdvanceCeremonyStep()
        {
            ceremonyStep.Value++;

            List<long> order = ceremonyOrder.Value;
            long localId = Game1.player.UniqueMultiplayerID;

            switch (ceremonyStep.Value)
            {
                case 1: // Opening line
                    Game1.drawObjectDialogue(this.CeremonyLine(Def.CeremonyOpeningLine));
                    break;

                case 2: // Announce 3rd place
                    if (order.Count >= 3)
                        Game1.drawObjectDialogue(
                            this.CeremonyLine(Def.CeremonyThirdPlaceLine, this.GetRacerName(order[2])));
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 3: // Prize for 3rd
                    if (order.Count >= 3 && order[2] == localId)
                        AwardPrizes(Def.ThirdPlacePrizes);
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 4: // Announce 2nd place
                    if (order.Count >= 2)
                        Game1.drawObjectDialogue(
                            this.CeremonyLine(Def.CeremonySecondPlaceLine, this.GetRacerName(order[1])));
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 5: // Prize for 2nd
                    if (order.Count >= 2 && order[1] == localId)
                        AwardPrizes(Def.SecondPlacePrizes);
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 6: // Announce 1st place
                    if (order.Count >= 1)
                    {
                        Game1.drawObjectDialogue(
                            this.CeremonyLine(Def.CeremonyFirstPlaceLine, this.GetRacerName(order[0])));
                        Game1.playSound("achievement");
                    }
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 7: // Prize for 1st
                    if (order.Count >= 1 && order[0] == localId)
                    {
                        this.RecordSpringFestivalWin();
                        AwardPrizes(Def.FirstPlacePrizes);
                    }
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 8: // Bet result: resolved privately, delivered via mail
                    bool hasBet = (betTargetFarmerId.Value.HasValue || betTargetNpcName.Value != null) && betAmount.Value > 0;
                    if (hasBet)
                        this.DeliverBetResult();
                    this.AdvanceCeremonyStep();
                    break;

                case 9: // Closing line
                    Game1.drawObjectDialogue(this.CeremonyLine(Def.CeremonyClosingLine, Def.FestivalDisplayName));
                    break;

                case CeremonyEndStep: // Hold until every player has finished, then all leave together
                    this.WaitForCeremonyEnd();
                    break;
            }
        }

        /// <summary>Barrier at the end of the awards ceremony. Players read the host's lines and open their
        /// prize menus at their own pace, so without this the first player to click through would warp home
        /// while the others were still mid-ceremony. Everyone who reaches the end parks in a non-cancelable
        /// ReadyCheckDialog (auto-readies each tick, exactly like the race-start barrier) and the festival
        /// only ends once all online players have arrived, so the whole party leaves at the same moment.</summary>
        private void WaitForCeremonyEnd()
        {
            // EndFestival fades out rather than leaving the Ceremony phase immediately, so UpdateCeremony
            // keeps ticking through the fade; latch here or the barrier would reopen and re-end.
            if (ceremonyEnded.Value)
                return;

            string? barrierId = ceremonyEndBarrierId.Value;
            if (!Game1.IsMultiplayer || barrierId == null)
            {
                ceremonyEnded.Value = true;
                this.EndFestival();
                return;
            }

            // Only reached with no menu open (UpdateCeremony holds while one is up), so if the dialog
            // was dismissed some other way this re-raises it instead of stranding the player.
            Logger.LogVerbose($"Ceremony finished locally; waiting on other players at barrier {barrierId}.");
            Game1.activeClickableMenu = new ReadyCheckDialog(
                barrierId,
                allowCancel: false,
                onConfirm: (_) =>
                {
                    Game1.exitActiveMenu();
                    ceremonyEnded.Value = true;
                    Logger.LogVerbose("All players finished the ceremony. Ending festival.");
                    this.EndFestival();
                });
        }

        private static string GetFarmerName(long uniqueId)
        {
            Farmer? farmer = uniqueId == Game1.player.UniqueMultiplayerID
                ? Game1.player
                : Game1.getAllFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == uniqueId);
            if (farmer == null) return "Unknown";
            string horsePart = farmer.mount != null ? $" on {farmer.mount.Name}" : "";
            return farmer.Name + horsePart;
        }

        private string GetRacerName(long uniqueId)
        {
            NpcRacer? racer = npcRacers.Find(r => r.FakeId == uniqueId);
            if (racer != null)
                return racer.Rider?.displayName ?? racer.Rider?.Name ?? "Mystery Rider";
            return GetFarmerName(uniqueId);
        }

        /// <summary>Hidden mail flag marking that a player has won the Spring Horse Festival race.
        /// Farmer.mailReceived is net-synced, so the host sees farmhand wins too; this is what
        /// unlocks Robin's bus-trailer offer (see <see cref="BusTrailerManager"/>).</summary>
        internal const string SpringWinFlagMailId = "HorseTycoon.SpringFestivalWin";
        /// <summary>Lewis's congratulation letter, queued the night of the win (Data/Mail entries
        /// added in ModEntry). The Bus variant is sent when the bus is already repaired and adds a
        /// P.S. pointing the player at Robin's horse trailer.</summary>
        internal const string SpringWinLetterMailId = "HorseTycoon.SpringFestivalWinLetter";
        internal const string SpringWinLetterBusMailId = "HorseTycoon.SpringFestivalWinLetterBus";
        /// <summary>Lewis's news letter for everyone who *didn't* win, naming the winner. Sent to all
        /// other farmers (online and offline) the first time anyone on the farm wins.</summary>
        internal const string SpringWinSpectatorMailId = "HorseTycoon.SpringFestivalWinSpectator";
        internal const string SpringWinSpectatorBusMailId = "HorseTycoon.SpringFestivalWinSpectatorBus";
        /// <summary>Farm modData key holding the first spring-festival winner's name. Host-written
        /// (synced + saved) and injected into the spectator letters' text in ModEntry's Data/Mail edit.</summary>
        internal const string SpringWinnerNameKey = "Froshty.HorseTycoon/SpringFestivalWinnerName";

        /// <summary>Whether any player on this farm has ever won the Spring Horse Festival.</summary>
        internal static bool HasAnyPlayerWonSpringFestival()
            => Game1.getAllFarmers().Any(f => f.mailReceived.Contains(SpringWinFlagMailId));

        /// <summary>Runs on the local winner's client when they take 1st place. On the player's first
        /// spring win, sets the hidden win flag and queues Lewis's congratulation letter for tomorrow.
        /// If it's also the farm's first win, spreads the news so everyone else gets a letter too.</summary>
        private void RecordSpringFestivalWin()
        {
            if (Def.Season != "spring" || Game1.player.mailReceived.Contains(SpringWinFlagMailId))
                return;

            bool farmFirstWin = !HasAnyPlayerWonSpringFestival();
            Game1.player.mailReceived.Add(SpringWinFlagMailId);
            bool busRepaired = Game1.MasterPlayer.mailReceived.Contains("ccVault");
            Game1.addMailForTomorrow(busRepaired ? SpringWinLetterBusMailId : SpringWinLetterMailId);
            Logger.LogVerbose($"First spring festival win recorded for {Game1.player.Name}; Lewis letter queued (busRepaired={busRepaired}, farmFirstWin={farmFirstWin}).");

            if (!farmFirstWin)
                return;

            this.Helper.Multiplayer.SendMessage(
                new SpringWinMessage(Game1.player.Name),
                MsgSpringWin,
                modIDs: new[] { this.Helper.ModRegistry.ModID });
            this.HandleSpringWinNews(Game1.player.Name, isWinner: true);
        }

        /// <summary>Delivers the farm-first-win news. On the host, stamps the winner's name into farm
        /// modData (the spectator letter text reads it) and queues letters for offline farmhands; on
        /// every non-winner client, queues the local player's own spectator letter. Runs directly on
        /// the winner's client and via <see cref="MsgSpringWin"/> on every other client.</summary>
        private void HandleSpringWinNews(string winnerName, bool isWinner)
        {
            bool busRepaired = Game1.MasterPlayer.mailReceived.Contains("ccVault");
            string spectatorMail = busRepaired ? SpringWinSpectatorBusMailId : SpringWinSpectatorMailId;

            if (Game1.IsMasterGame)
            {
                Game1.getFarm().modData[SpringWinnerNameKey] = winnerName;

                // Offline farmhands: the host owns their save data, so queue their letters directly.
                // Online players (including the winner) are skipped; each queues their own locally.
                foreach (Farmer farmer in Game1.getAllFarmers())
                {
                    if (Game1.getOnlineFarmers().Contains(farmer))
                        continue;
                    if (!farmer.hasOrWillReceiveMail(spectatorMail))
                        farmer.mailForTomorrow.Add(spectatorMail);
                }
            }

            // Re-resolve Data/Mail so the spectator letters pick up the winner's name.
            this.Helper.GameContent.InvalidateCache("Data/Mail");

            if (!isWinner && !Game1.player.hasOrWillReceiveMail(spectatorMail))
                Game1.addMailForTomorrow(spectatorMail);
            Logger.LogVerbose($"Spring win news handled (winner={winnerName}, isWinner={isWinner}, host={Game1.IsMasterGame}).");
        }

        private static void AwardPrizes(params string[] itemIds)
        {
            var items = itemIds.Select(id => ItemRegistry.Create(id)).ToList<Item>();
            Game1.activeClickableMenu = new CeremonyPrizeMenu(items);
        }

        /// <summary>The prize menu handed out at the awards ceremony.
        ///
        /// Closing a vanilla <see cref="ItemGrabMenu"/> while an event is running bumps that event's
        /// command pointer: <c>MenuWithInventory.receiveLeftClick</c> (the OK button) and
        /// <c>ItemGrabMenu.receiveKeyPress</c> (Esc) both do <c>currentEvent.CurrentCommand++</c>, so an
        /// event script that opened a chest resumes once the player is done with it. Our festival event is
        /// idle during the ceremony, so that bump instead runs the next set-up command and kicks off a
        /// screen fade. Snapshot the pointer and put it back inside the same input call, which lands before
        /// the event updates later in the tick, so nothing ever sees the advanced value.</summary>
        private class CeremonyPrizeMenu : ItemGrabMenu
        {
            public CeremonyPrizeMenu(IList<Item> items)
                : base(items, false, true, InventoryMenu.highlightAllItems, null, null)
            {
            }

            private static int CurrentEventCommand => Game1.currentLocation?.currentEvent?.CurrentCommand ?? -1;

            private static void RestoreEventCommand(int command)
            {
                Event? ev = Game1.currentLocation?.currentEvent;
                if (ev != null && command >= 0 && ev.CurrentCommand != command)
                    ev.CurrentCommand = command;
            }

            public override void receiveLeftClick(int x, int y, bool playSound = true)
            {
                int command = CurrentEventCommand;
                base.receiveLeftClick(x, y, playSound);
                RestoreEventCommand(command);
            }

            public override void receiveKeyPress(Microsoft.Xna.Framework.Input.Keys key)
            {
                int command = CurrentEventCommand;
                base.receiveKeyPress(key);
                RestoreEventCommand(command);
            }
        }

        private void EndFestival()
        {
            Game1.viewportFreeze = false;
            Game1.player.CanMove = true;

            this.ReleaseFestivalMount();

            // Do NOT despawn NPCs here: the temp map is still active during the fade and
            // they must stay visible until the screen is fully black. Reset() clears the
            // lists once the player has warped back to the real Forest.
            Event? festival = RaceFestival;
            if (festival != null)
            {
                festival.endBehaviors(new[] { "end" }, Game1.currentLocation);

                // Festivals normally spit you out at the farmhouse door. A bus festival is a round trip,
                // so the bus drops everyone back where it picked them up. exitEvent (run by endBehaviors)
                // has already set the farm exit at this point, so this has to override it afterwards.
                FestivalDefinition? def = activeDef.Value;
                if (def?.BusArrival == true)
                    festival.setExitLocation(def.BusReturnLocation, def.BusReturnTile.X, def.BusReturnTile.Y);
            }
        }

        /// <summary>
        /// Loads the player's horses back onto the bus: temporary bus horses are despawned and unridden
        /// stable horses are sent home, so nothing is left grazing on the festival map. Called when the
        /// bus doors shut (they visibly board while the trailer shows their heads) and again from
        /// <see cref="Reset"/> as the catch-all for every other way the festival can end.
        /// </summary>
        private void StowBusHorses()
        {
            foreach (Horse busHorse in busFestivalHorses.Value)
            {
                if (HorseHelper.IsManagedStableHorse(busHorse))
                {
                    // Send this exact character home instead of Stable.grabHorse(): the festival runs on
                    // a temporary map that Utility.findHorse can't see, so grabHorse decides the horse has
                    // gone missing and spawns a fresh blank one — losing the skin and tack modData, which
                    // leaves the horse standing in its stable with the vanilla brown texture.
                    if (busHorse.rider == null)
                        ReturnHorseToStable(busHorse);
                }
                else
                {
                    busHorse.currentLocation?.characters.Remove(busHorse);
                }
            }
            busFestivalHorses.Value.Clear();
        }

        /// <summary>Dismounts the player and sends whatever they were riding back where it belongs, so
        /// nothing is left standing on the festival map. Safe to call twice.</summary>
        private void ReleaseFestivalMount()
        {
            Horse? mount = Game1.player.mount;
            if (mount == null)
                return;

            bool isBorrowed = borrowedFestivalHorse.Value == mount;

            mount.rider = null;
            mount.dismounting.Value = false;
            mount.mounting.Value = false;
            mount.controller = null;
            Game1.player.mount = null;

            if (isBorrowed)
            {
                // Borrowed horse: pull it from the festival location; Reset() clears the ref.
                mount.currentLocation?.characters.Remove(mount);
                borrowedFestivalHorse.Value = null;
            }
            else
            {
                // Player's own horse: return it to its stable on the farm.
                ReturnHorseToStable(mount);
            }
        }

        private static void ReturnHorseToStable(Horse horse)
        {
            Farm farm = Game1.getFarm();
            Stable? stable = farm.buildings.OfType<Stable>()
                .FirstOrDefault(s => s.HorseId == horse.HorseId);

            horse.currentLocation?.characters.Remove(horse);
            horse.rider = null;
            horse.dismounting.Value = false;
            horse.mounting.Value = false;
            horse.controller = null;
            horse.EventActor = false;

            if (stable != null)
            {
                horse.Position = new Vector2((stable.tileX.Value + 1) * 64f, (stable.tileY.Value + 1) * 64f);
                horse.faceDirection(Game1.down);
            }

            horse.currentLocation = farm;
            horse.Halt();
            if (!farm.characters.Contains(horse))
                farm.characters.Add(horse);
        }

        // ======================== NPC Racer System ========================

        /// <summary>
        /// Pull Marnie, Leah, and Abigail out of their current world locations and place them
        /// in the festival location so they are visible during the pasture phase and available
        /// when the race starts. Called from <see cref="EnterPasture"/>; guarded against double-run.
        /// </summary>
        private void SpawnNpcRiders()
        {
            if (npcRidersBorrowed) return;
            npcRidersBorrowed = true;

            GameLocation festLoc = Game1.currentLocation;

            for (int i = 0; i < Def.NpcRiderNames.Length; i++)
            {
                string name = Def.NpcRiderNames[i];
                Point holdTile = i < NpcRiderHoldingTiles.Length
                    ? NpcRiderHoldingTiles[i]
                    : new Point(92, 20 + i * 2);
                var actor = new NPC(
                    new AnimatedSprite("Characters/" + name, 0, 16, 32),
                    TileToPixels(holdTile),
                    Game1.left,
                    name);
                actor.EventActor = true;
                ApplyFestivalAppearance(actor);
                festLoc.characters.Add(actor);
                spawnedRiders.Add(actor);
                Logger.LogVerbose($"Spawned rider actor '{name}' at holding tile {holdTile}.");
            }
        }

        /// <summary>
        /// Remove all spawned rider event actors from the festival location.
        /// Safe to call multiple times.
        /// </summary>
        private void DespawnNpcRiders()
        {
            var festLoc = Game1.currentLocation;
            foreach (NPC actor in spawnedRiders)
                festLoc?.characters.Remove(actor);
            spawnedRiders.Clear();
            npcRidersBorrowed = false;
        }

        // sveOnly: skip vanilla Characters tileset tiles (used when reading Set-Up, where loadActors
        // already handles them; processing them here would create duplicate event actors).
        private List<NpcSpectatorPlacement> ReadNpcPlacements(string layerName, bool sveOnly = false)
        {
            var results = new List<NpcSpectatorPlacement>();
            var placeholderSlots = new List<(Point Tile, int Direction)>();

            var layer = Game1.currentLocation?.map.GetLayer(layerName);
            if (layer == null) return results;

            for (int y = 0; y < layer.LayerHeight; y++)
                for (int x = 0; x < layer.LayerWidth; x++)
                {
                    var tile = layer.Tiles[x, y];
                    if (tile?.TileSheet == null) continue;

                    int npcIdx = tile.TileIndex / 4;
                    int dir = tile.TileIndex % 4;

                    if (!sveOnly && tile.TileSheet.Id == "Characters")
                    {
                        if (npcIdx < CharacterTileNames.Length)
                            results.Add(new NpcSpectatorPlacement(CharacterTileNames[npcIdx], new Point(x, y), dir));
                        else
                            placeholderSlots.Add((new Point(x, y), dir));
                    }
                    else if (tile.TileSheet.Id == "SVECharacters"
                          && tile.TileIndex >= SveCharacterSheetPadding
                          && (tile.TileIndex - SveCharacterSheetPadding) / 4 < SveCharacterTileNames.Length)
                    {
                        int sveIdx = (tile.TileIndex - SveCharacterSheetPadding) / 4;
                        string name = SveCharacterTileNames[sveIdx];
                        if (Game1.characterData?.ContainsKey(name) != true)
                            Logger.LogVerbose($"ReadNpcPlacements('{layerName}'): skipping '{name}', not in characterData (SVE not installed?).");
                        else
                            results.Add(new NpcSpectatorPlacement(name, new Point(x, y), dir));
                    }
                    else if (tile.TileSheet.Id == "ESCharacters"
                          && tile.TileIndex >= EsCharacterSheetPadding
                          && (tile.TileIndex - EsCharacterSheetPadding) / 4 < EsCharacterTileNames.Length)
                    {
                        int esIdx = (tile.TileIndex - EsCharacterSheetPadding) / 4;
                        string name = EsCharacterTileNames[esIdx];
                        if (Game1.characterData?.ContainsKey(name) != true)
                            Logger.LogVerbose($"ReadNpcPlacements('{layerName}'): skipping '{name}', not in characterData (East Scarp not installed?).");
                        else
                            results.Add(new NpcSpectatorPlacement(name, new Point(x, y), dir));
                    }
                }

            // Remove NPCs that require a prior meeting if no attending farmer has met them yet.
            var farmers = Game1.getAllFarmers().ToList();
            results.RemoveAll(p =>
            {
                if (!MetRequiredNpcNames.Contains(p.Name)) return false;
                if (farmers.Any(f => f.friendshipData.ContainsKey(p.Name))) return false;
                Logger.LogVerbose($"ReadNpcPlacements('{layerName}'): skipping '{p.Name}', not met by any attending farmer.");
                return true;
            });

            // Leo only attends once he has moved to Pelican Town.
            if (!Game1.MasterPlayer.mailReceived.Contains("leoMoved"))
            {
                int removed = results.RemoveAll(p => p.Name == "Leo");
                if (removed > 0)
                    Logger.LogVerbose($"ReadNpcPlacements('{layerName}'): skipping 'Leo', not yet moved to Pelican Town.");
            }

            if (placeholderSlots.Count > 0)
            {
                var modNpcs = this.GetMetModNpcNames();
                for (int i = 0; i < placeholderSlots.Count; i++)
                {
                    if (i >= modNpcs.Count) break;
                    var (tile, dir) = placeholderSlots[i];
                    results.Add(new NpcSpectatorPlacement(modNpcs[i], tile, dir, IsAutoFilled: true));
                    Logger.LogVerbose($"ReadNpcPlacements('{layerName}'): assigned mod NPC '{modNpcs[i]}' to placeholder at {tile}.");
                }
                if (modNpcs.Count < placeholderSlots.Count)
                    Logger.LogVerbose($"ReadNpcPlacements('{layerName}'): {placeholderSlots.Count - modNpcs.Count} placeholder slot(s) left unfilled (not enough met mod NPCs).");
            }

            Logger.LogVerbose($"ReadNpcPlacements('{layerName}'): {results.Count} entries ({placeholderSlots.Count} placeholder slot(s)).");
            return results;
        }

        /// <summary>
        /// Returns names of mod-added NPCs (not in the vanilla CharacterTileNames list) that at least
        /// one attending online farmer has already met, in a deterministic shuffled order.
        /// </summary>
        private List<string> GetMetModNpcNames()
        {
            var vanillaNames = new HashSet<string>(CharacterTileNames, System.StringComparer.OrdinalIgnoreCase);
            // SVE and ES NPCs are excluded entirely from auto-fill (placed manually in the map).
            var manuallyPlacedNames = new HashSet<string>(
                SveCharacterTileNames.Concat(EsCharacterTileNames),
                System.StringComparer.OrdinalIgnoreCase);

            var allNames = Game1.characterData?.Keys;
            if (allNames == null) return new List<string>();

            var farmers = Game1.getAllFarmers().ToList();
            var met = allNames
                .Where(name => !vanillaNames.Contains(name)
                    && !manuallyPlacedNames.Contains(name)
                    && farmers.Any(f => f.friendshipData.ContainsKey(name)))
                .ToList();

            // Shuffle deterministically so all clients assign the same mod NPC to each placeholder.
            var rng = new System.Random((int)(Game1.uniqueIDForThisGame ^ (uint)Game1.Date.TotalDays));
            for (int i = met.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (met[i], met[j]) = (met[j], met[i]);
            }

            Logger.LogVerbose($"GetMetModNpcNames: found {met.Count} met mod NPC(s): {string.Join(", ", met)}");
            return met;
        }

        private static readonly string[] AutoFilledSpectatorDialogKeys =
        {
            "FestivalSpectator_Dialog_0",
            "FestivalSpectator_Dialog_1",
            "FestivalSpectator_Dialog_2",
            "FestivalSpectator_Dialog_3",
            "FestivalSpectator_Dialog_4",
            "FestivalSpectator_Dialog_5",
            "FestivalSpectator_Dialog_6",
            "FestivalSpectator_Dialog_7",
        };

        private void SpawnSpectators(List<NpcSpectatorPlacement>? placements)
        {
            if (placements == null) return;
            var festLoc = Game1.currentLocation;
            if (festLoc == null) return;
            foreach (var p in placements)
            {
                // Create a fresh event-actor NPC, same approach as the game's addTemporaryActor command.
                // This leaves the real NPC untouched at home with their normal animation state.
                var sprite = new AnimatedSprite("Characters/" + p.Name, 0, 16, 32);
                var actor = new NPC(sprite, TileToPixels(p.Tile), p.Direction, p.Name);
                actor.EventActor = true;
                ApplyFestivalAppearance(actor);
                actor.faceDirection(p.Direction);
                actor.Sprite.StopAnimation();
                if (p.IsAutoFilled)
                {
                    // Auto-filled NPCs have no festival-specific dialog, so give them a generic line.
                    // Pick deterministically from the NPC's name so all clients agree.
                    int idx = System.Math.Abs(p.Name.GetHashCode()) % AutoFilledSpectatorDialogKeys.Length;
                    string text = this.Helper.Translation.Get(AutoFilledSpectatorDialogKeys[idx]);
                    actor.TemporaryDialogue = new System.Collections.Generic.Stack<Dialogue>();
                    actor.TemporaryDialogue.Push(new Dialogue(actor, "HorseTycoon.spectator." + p.Name, text));
                }
                else if (RaceFestival is Event fest
                      && fest.TryGetFestivalDialogueForYear(actor, p.Name, out Dialogue festivalDialogue))
                {
                    // Named SVE/ES spectators are spawned by us (not the game's loadActors, which the
                    // 144-tile sheet padding deliberately hides them from), so the game never applies
                    // their Data/Festivals/<key>:<name> line. Apply it here, isolated to this event
                    // actor via TemporaryDialogue so the real NPC's shared dialogue cache is untouched.
                    actor.TemporaryDialogue = new System.Collections.Generic.Stack<Dialogue>();
                    actor.TemporaryDialogue.Push(festivalDialogue);
                }
                RaceFestival?.actors.Add(actor);
                spawnedSpectators.Add(actor);
            }
        }

        /// <summary>
        /// Give every actor currently in the festival the plain, season-appropriate look from
        /// Data/Characters, never a location-specific work outfit (e.g. Shane's JojaMart uniform).
        /// Covers the actors the game's own <c>loadActors Set-Up</c> command created as well as ours.
        /// </summary>
        private void ApplyFestivalAppearances()
        {
            if (RaceFestival == null) return;
            foreach (NPC actor in RaceFestival.actors)
                ApplyFestivalAppearance(actor);
        }

        /// <summary>
        /// Pick <paramref name="actor"/>'s appearance as if they were standing on the festival map, then
        /// freeze it for the rest of the festival.
        ///
        /// Event actors never get a <c>currentLocation</c> (neither Event.actors nor Event.update assigns
        /// one, and the temp festival map is called "Temp" so the game's own addActor can't look it up),
        /// and <see cref="NPC.ChooseAppearance"/> bails out early when it's null. So an actor keeps
        /// whatever textures it happened to be constructed with: no portrait at all for the ones we spawn,
        /// no seasonal variant (e.g. Shane_Winter) for anyone, and any outfit left over from wherever the
        /// NPC last was, which is how Shane turns up at the festival in his Joja uniform. Point the actor
        /// at the festival map just long enough for vanilla to pick the entry, then detach it again so
        /// nothing else treats the actor as living there.
        ///
        /// The festival map is outdoors, in the festival's season, and is never JojaMart, so the seasonal
        /// entries apply and the work-outfit ones can't.
        /// </summary>
        private static void ApplyFestivalAppearance(NPC actor)
        {
            // Stall keepers deliberately borrow another character's sheet (see SpawnShopNpc), which is
            // exactly what this would undo; they turn off dynamic appearance to say so.
            if (!actor.AllowDynamicAppearance)
                return;

            // Only ever touch throwaway festival copies. The overrides below are permanent, so applying
            // them to a real villager would pin their outfit for the rest of the save.
            if (!actor.EventActor)
                return;

            GameLocation? previous = actor.currentLocation;
            try
            {
                actor.currentLocation = Game1.currentLocation;
                actor.ChooseAppearance();
            }
            catch (System.Exception ex)
            {
                Logger.LogVerbose($"Could not choose a festival appearance for '{actor.Name}': {ex.Message}");
            }
            finally
            {
                actor.currentLocation = previous;
            }

            // Lock the textures in: TryLoadPortraits/TryLoadSprites become no-ops while these are set, so
            // nothing (a stray ChooseAppearance, an asset reload) can swap in another outfit mid-festival.
            actor.portraitOverridden = true;
            actor.spriteOverridden = true;
            actor.AllowDynamicAppearance = false;
        }

        private void DespawnSpectators()
        {
            foreach (NPC actor in spawnedSpectators)
                RaceFestival?.actors.Remove(actor);
            spawnedSpectators.Clear();
        }


        /// <summary>
        /// Spawn one Horse + rider pair per NPC racer entry into the stalls immediately after
        /// the player slots. Idempotent, guarded by <see cref="npcRacersSpawned"/>.
        /// </summary>
        private void SpawnNpcRacers(GameLocation loc, int playerSlotCount)
        {
            if (npcRacersSpawned) return;
            npcRacersSpawned = true;

            this.SpawnNpcRiders();

            var rng = new System.Random(
                (int)(Game1.uniqueIDForThisGame ^ (uint)Game1.Date.TotalDays));

            int npcSlots = System.Math.Max(0, MaxRacers - playerSlotCount);
            if (npcSlots < Def.NpcRiderNames.Length)
                this.Monitor.Log(
                    $"Race is full ({playerSlotCount} players, max {MaxRacers}). Dropping {Def.NpcRiderNames.Length - npcSlots} NPC racer(s).",
                    LogLevel.Info);

            bool anyPlayerHorseIsfast = Game1.getAllFarmers()
                .Select(f => f.mount)
                .Where(m => m != null)
                .Select(m => HorseHelper.GetFarmAnimalForHorse(m))
                .Where(a => a != null)
                .Any(a => a!.GetHorseStats().TotalSpeed >= 40);

            for (int i = 0; i < System.Math.Min(Def.NpcRiderNames.Length, npcSlots); i++)
            {
                string riderName = Def.NpcRiderNames[i];
                int slot = playerSlotCount + i;
                Point stallTile = StallHorseTile(slot);

                // The rider NPC must already be in the festival location (placed by the event script).
                NPC? rider = loc.characters.OfType<NPC>()
                    .FirstOrDefault(c => c.Name == riderName);
                if (rider == null)
                {
                    this.Monitor.Log(
                        $"NPC racer '{riderName}' not found in festival location, skipping.",
                        LogLevel.Warn);
                    continue;
                }

                // Create the AI horse in the stall.
                var horse = new Horse(System.Guid.NewGuid(), stallTile.X, stallTile.Y);
                horse.Name = riderName + "RaceHorse";
                horse.modData[HorseHelper.HorseSkinKey] = AllSkins[rng.Next(AllSkins.Length)];
                horse.modData[HorseHelper.OverlaysKey] = "Saddle,Bridle";
                horse.currentLocation = loc;
                horse.Position = TileToPixels(stallTile);
                horse.Halt();
                horse.faceDirection(Game1.right);
                // Suppress Horse.update() so it can't overwrite the gallop animation with idle frames.
                horse.EventActor = true;
                if (!loc.characters.Contains(horse))
                    loc.characters.Add(horse);
                HorseAnimations.SetIdle(horse);

                // Seat the rider on the horse. drawOnTop ensures the rider renders above the horse sprite.
                rider.EventActor = true;
                rider.drawOnTop = true;
                SyncRiderToHorse(rider, horse);

                int yearBonus = anyPlayerHorseIsfast ? 5 : 0;
                int speedIV = Def.NpcRiderSpeeds[i % Def.NpcRiderSpeeds.Length] + yearBonus;
                int sprintIV = Def.NpcRiderSprints[i % Def.NpcRiderSprints.Length] + yearBonus;
                int jumpIV = Def.NpcRiderJumps != null
                    ? Def.NpcRiderJumps[i % Def.NpcRiderJumps.Length]
                    : 0;

                bool useJumpRoute = jumpIV >= Def.NpcJumpMinSkill
                    && Def.NpcJumpRoutes != null
                    && Def.NpcJumpRoutes.Length > 0;
                Point[] route = useJumpRoute
                    ? Def.NpcJumpRoutes![i % Def.NpcJumpRoutes.Length]
                    : Def.NpcRaceRoutes[i == 0 ? 2 : i % Def.NpcRaceRoutes.Length];

                var racer = new NpcRacer
                {
                    Horse = horse,
                    Rider = rider,
                    FakeId = nextNpcFakeId--,
                    TotalSpeed = speedIV,
                    TotalSprint = sprintIV,
                    TotalJump = jumpIV,
                    Route = route,
                    WaypointIndex = 0,
                    NextSprintCheckMs = (float)(rng.NextDouble() * 5000.0 + 3000.0),
                };
                npcRacers.Add(racer);

                Logger.LogVerbose($"NPC racer '{riderName}' in slot {slot}: Speed={speedIV}, Sprint={sprintIV}, Jump={jumpIV} ({(useJumpRoute ? "jump route" : "detour route")})");
            }

            // The two fastest NPCs track the race leader; the rest track the nearest farmer.
            foreach (NpcRacer r in npcRacers.OrderByDescending(r => r.TotalSpeed).Take(2))
                r.MatchLeader = true;

            this.LoadNpcJumpZonesFromMap(loc);
            this.LoadGoingFromMap(loc);
        }

        // Layer names used to author NPC jump zones in Tiled. Place approach-marker tiles on
        // NpcJumpApproach and landing-marker tiles on NpcJumpLanding; pairs are matched by
        // scan order (top→bottom, left→right). Set a "MinSkill" tile property on each approach
        // tile in the tileset to control the skill threshold; omitting it falls back to NpcJumpMinSkill.
        private const string NpcJumpApproachLayer = "NpcJumpApproach";
        private const string NpcJumpLandingLayer = "NpcJumpLanding";
        // NPC jump arcs are capped at this many tiles regardless of zone definition or skill.
        private const float MaxNpcJumpTiles = 4f;
        // When A* fails to reach a waypoint, wait this long before retrying instead of
        // recomputing a full path every frame (the per-frame recompute caused heavy lag).
        private const float PathRetryDelayMs = 250f;
        // After this many failed attempts, skip the unreachable waypoint so the NPC can
        // never get permanently stranded (restores the pre-jump-code resilient behavior).
        private const int MaxPathRetries = 8;

        /// <summary>
        /// Scans the festival map for NpcJumpApproach and NpcJumpLanding tile layers and
        /// populates <see cref="FestivalDefinition.NpcJumpZones"/> for this race session.
        /// Pairs are matched by top→bottom left→right scan order; counts must be equal.
        /// </summary>
        private void LoadNpcJumpZonesFromMap(GameLocation loc)
        {
            // Zones authored in code (FestivalDefinition.NpcJumpZones) take precedence: the map's
            // scan-order pairing can't express curving tracks with vertical/westward chained jumps, so
            // festivals with complex courses define zones explicitly and skip the map scan entirely.
            if (Def.NpcJumpZones.Count > 0)
            {
                this.Monitor.Log($"Using {Def.NpcJumpZones.Count} code-defined NPC jump zone(s); skipping map scan.", LogLevel.Info);
                return;
            }

            var approachLayer = loc.map.GetLayer(NpcJumpApproachLayer);
            var landingLayer = loc.map.GetLayer(NpcJumpLandingLayer);

            if (approachLayer == null && landingLayer == null)
                return; // no jump zones on this map, silent rather than an error

            if (approachLayer == null || landingLayer == null)
            {
                this.Monitor.Log(
                    $"Map has {NpcJumpApproachLayer} or {NpcJumpLandingLayer} but not both. NPC jump zones disabled.",
                    LogLevel.Warn);
                return;
            }

            int w = approachLayer.LayerWidth;
            int h = approachLayer.LayerHeight;

            var approaches = new List<(Point Tile, int MinSkill)>();
            var landings = new List<Point>();

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var approachTile = approachLayer.Tiles[x, y];
                    if (approachTile != null)
                    {
                        // Read MinSkill from tileset property first, then per-tile override.
                        string? raw = null;
                        approachTile.TileIndexProperties.TryGetValue("MinSkill", out var tv);
                        if (tv != null) raw = tv.ToString();
                        if (raw == null) { approachTile.Properties.TryGetValue("MinSkill", out var pv); raw = pv?.ToString(); }
                        int minSkill = int.TryParse(raw, out int parsed) ? parsed : Def.NpcJumpMinSkill;
                        approaches.Add((new Point(x, y), minSkill));
                    }

                    if (landingLayer.Tiles[x, y] != null)
                        landings.Add(new Point(x, y));
                }

            if (approaches.Count != landings.Count)
            {
                this.Monitor.Log(
                    $"{NpcJumpApproachLayer} has {approaches.Count} tile(s) but {NpcJumpLandingLayer} has {landings.Count}; counts must match. NPC jump zones disabled.",
                    LogLevel.Warn);
                return;
            }

            for (int i = 0; i < approaches.Count; i++)
            {
                var (tile, minSkill) = approaches[i];
                Def.NpcJumpZones[tile] = new NpcJumpZone { LandingTile = landings[i], MinSkill = minSkill };
                Logger.LogVerbose($"[Jump] Zone {i}: approach {tile} → landing {landings[i]} (MinSkill={minSkill})");
            }

            this.Monitor.Log($"Loaded {Def.NpcJumpZones.Count} NPC jump zone(s) from map.", LogLevel.Info);
        }

        /// <summary>
        /// Called every Racing tick. Moves each NPC horse directly toward its current waypoint
        /// at a stat-scaled speed, syncs the rider position, and checks the finish band.
        /// Waypoints are predefined to follow the open navigable paths on the course map, so
        /// direct interpolation is equivalent to path-following for collision purposes.
        /// </summary>
        private void UpdateNpcRacers()
        {
            if (startCountdown.Value >= 0f) return;

            GameLocation loc = Game1.currentLocation;
            int deltaMs = Game1.currentGameTime.ElapsedGameTime.Milliseconds;

            for (int npcIdx = 0; npcIdx < npcRacers.Count; npcIdx++)
            {
                NpcRacer r = npcRacers[npcIdx];

                // Stop only when the full route is exhausted, not on finish crossing, so the
                // horse rides through the finish band to its final waypoint past the line.
                if (r.PathIndex >= r.ComputedPath.Count && r.WaypointIndex >= r.Route.Length)
                {
                    if (!r.MovementDone)
                    {
                        r.MovementDone = true;
                        HorseAnimations.SetGrazing(r.Horse);
                    }
                    continue;
                }

                // Drive jump arc: while airborne, interpolate position and parabolic Y offset.
                if (r.IsJumping)
                {
                    r.JumpTimer += deltaMs;
                    float t = System.Math.Clamp(r.JumpTimer / r.JumpDuration, 0f, 1f);
                    r.Horse.Position = Vector2.Lerp(r.JumpStart, r.JumpEnd, t);
                    r.Horse.drawOffset = new Vector2(0f, -r.JumpPeakHeight * 4f * t * (1f - t));
                    r.Horse.drawOnTop = true;

                    if (r.JumpTimer >= r.JumpDuration)
                    {
                        r.IsJumping = false;
                        r.Horse.Position = r.JumpEnd;
                        r.Horse.drawOffset = Vector2.Zero;
                        r.Horse.drawOnTop = false;
                        // For forward jumps, start a cooldown so the NPC visibly touches down
                        // on the landing platform before the next zone can trigger.
                        if (r.JumpEnd != r.JumpStart)
                            r.JumpCooldownMs = 300f;
                    }
                    if (r.Rider != null) SyncRiderToHorse(r.Rider, r.Horse);
                    continue;
                }

                // Suppress pathfinding and movement while the post-jump cooldown is active so
                // the NPC stays on the landing platform long enough for the next zone to trigger.
                if (r.JumpCooldownMs > 0f)
                {
                    r.JumpCooldownMs -= deltaMs;
                    continue;
                }

                // Zone check runs BEFORE A* so a zone trigger cannot accidentally consume
                // WaypointIndex. If A* ran first and succeeded, WaypointIndex would advance on
                // the same tick the zone fires, and then after the jump(s) the route-exhausted check
                // at the top would see WaypointIndex >= Route.Length and stall the NPC.
                if (Def.NpcJumpZones.Count > 0)
                {
                    Point currentTile = new((int)r.Horse.Tile.X, (int)r.Horse.Tile.Y);

                    // Clear the guard once the NPC moves off the tile that last triggered a jump,
                    // so chained jumps (landing = new takeoff) and future revisits work correctly.
                    if (r.LastJumpApproachTile.HasValue && currentTile != r.LastJumpApproachTile.Value)
                        r.LastJumpApproachTile = null;

                    if (r.LastJumpApproachTile == null && Def.NpcJumpZones.TryGetValue(currentTile, out NpcJumpZone? zone))
                    {
                        r.LastJumpApproachTile = currentTile;
                        r.IsJumping = true;
                        r.JumpTimer = 0f;
                        r.JumpStart = r.Horse.Position;
                        r.JumpPeakHeight = 20f + r.TotalJump * 0.5f;

                        // Face the horse (and rider) toward the landing tile before the arc starts.
                        Vector2 jumpDir = new Vector2(zone.LandingTile.X - currentTile.X, zone.LandingTile.Y - currentTile.Y);
                        int jumpFacing = GetFacingDirection(jumpDir);
                        r.Horse.faceDirection(jumpFacing);
                        if (r.Rider != null) r.Rider.faceDirection(jumpFacing);
                        // Exhaust the current A* path so that after landing the next-tick A*
                        // check recomputes from the landing tile. Without this, PathIndex may
                        // still point at the approach tile (jump fired before the NPC reached
                        // the tile center) and the movement loop would send it back there.
                        r.PathIndex = r.ComputedPath.Count;

                        float tileDist = Vector2.Distance(
                            new Vector2(currentTile.X, currentTile.Y),
                            new Vector2(zone.LandingTile.X, zone.LandingTile.Y));
                        bool clears = r.TotalJump >= zone.MinSkill && tileDist <= MaxNpcJumpTiles;
                        if (clears)
                        {
                            // Skill sufficient and within max distance: arc forward to the landing tile.
                            r.JumpEnd = TileToPixels(zone.LandingTile);
                            float jumpDist = Vector2.Distance(r.JumpStart, r.JumpEnd);
                            float speedPxPerMs = ComputeNpcSpeedPixelsPerMs(r);
                            r.JumpDuration = jumpDist / System.Math.Max(speedPxPerMs, 0.001f);
                            Logger.LogVerbose($"[Jump] {r.Rider?.Name ?? r.Horse.Name} clears obstacle at {currentTile} (skill {r.TotalJump} >= {zone.MinSkill}, dist {tileDist:F1} tiles)");
                        }
                        else
                        {
                            // Skill too low or obstacle too wide: blocked hop, arc in place, no forward progress.
                            r.JumpEnd = r.JumpStart;
                            r.JumpDuration = 600f; // fixed penalty duration in ms
                            Logger.LogVerbose($"[Jump] {r.Rider?.Name ?? r.Horse.Name} blocked hop at {currentTile} (skill {r.TotalJump}, dist {tileDist:F1} tiles, minSkill {zone.MinSkill}, max {MaxNpcJumpTiles})");
                        }
                        continue; // skip A* and movement this tick; WaypointIndex must not advance here
                    }
                }

                this.UpdateNpcSprint(r, deltaMs, npcIdx);

                // Compute A* path to next waypoint when the previous segment is exhausted.
                if (r.PathIndex >= r.ComputedPath.Count && r.WaypointIndex < r.Route.Length)
                {
                    if (Def.NpcJumpZones.Count == 0)
                    {
                        // Flat race: every waypoint is reachable by walking.
                        TryComputePathToWaypoint(r, loc, r.Route[r.WaypointIndex]);
                        r.WaypointIndex++;
                    }
                    // Jump race: an NPC may legitimately be stranded on a landing platform whose
                    // next waypoint is only reachable across a jump, so retry rather than skip.
                    // Retries are throttled and capped so a genuinely unreachable waypoint can
                    // neither thrash A* every frame (lag) nor jam the NPC permanently.
                    else if (r.PathRetryCooldownMs > 0f)
                    {
                        r.PathRetryCooldownMs -= deltaMs;
                    }
                    else if (TryComputePathToWaypoint(r, loc, r.Route[r.WaypointIndex]))
                    {
                        r.WaypointIndex++;
                        r.PathRetryCount = 0;
                    }
                    else if (++r.PathRetryCount >= MaxPathRetries)
                    {
                        Logger.LogVerbose($"[Path] {r.Rider?.Name ?? r.Horse.Name} giving up on waypoint {r.WaypointIndex} ({r.Route[r.WaypointIndex]}) after {r.PathRetryCount} failed A* attempts, skipping.");
                        r.WaypointIndex++;
                        r.PathRetryCount = 0;
                    }
                    else
                    {
                        r.PathRetryCooldownMs = PathRetryDelayMs;
                    }
                }

                // Drive position directly along the A*-computed tile path.
                // This bypasses MovePosition (blocked during eventUp) while still respecting
                // the collision-aware path that A* produced.
                float step = ComputeNpcSpeedPixelsPerMs(r) * deltaMs;
                // Match AI: when the NPC is more than 15 tiles from the nearest farmer, scale
                // their speed up or down based on who is further along the course.
                // Progress is measured by waypoint index on the NPC's own route so that raw
                // movement speed differences between NPCs and players don't skew the comparison.
                var racingFarmers = Game1.getAllFarmers().Where(f => f.currentLocation == loc).ToList();
                bool allPlayersFinished = racingFarmers.Count == 0 || racingFarmers.All(f => FinishOrder.Contains(f.UniqueMultiplayerID));
                if (r.AiMode == AiMode.Match && allPlayersFinished && r.LastMatchMultiplier != 1f)
                {
                    Logger.LogVerbose($"[Match AI] {r.Rider?.Name ?? r.Horse.Name}: all players finished, resuming normal speed");
                    r.LastMatchMultiplier = 1f;
                }
                if (r.AiMode == AiMode.Match && !allPlayersFinished)
                {
                    Farmer? targetFarmer = null;
                    float targetTileDist = float.MaxValue;

                    if (r.MatchLeader)
                    {
                        // Find the farmer farthest along the course (highest projected waypoint on this route).
                        int maxWpIdx = -1;
                        foreach (Farmer farmer in racingFarmers)
                        {
                            if (FinishOrder.Contains(farmer.UniqueMultiplayerID)) continue;
                            int wpIdx = NearestWaypointIndex(r.Route, farmer.Tile);
                            if (wpIdx > maxWpIdx) { maxWpIdx = wpIdx; targetFarmer = farmer; }
                        }
                        if (targetFarmer != null)
                            targetTileDist = Vector2.Distance(r.Horse.Tile, targetFarmer.Tile);
                    }
                    else
                    {
                        foreach (Farmer farmer in racingFarmers)
                        {
                            float d = Vector2.Distance(r.Horse.Tile, farmer.Tile);
                            if (d < targetTileDist) { targetTileDist = d; targetFarmer = farmer; }
                        }
                    }

                    bool applyMatch = targetFarmer != null && targetTileDist > 15f;
                    if (applyMatch)
                    {
                        int playerWpIdx = NearestWaypointIndex(r.Route, targetFarmer!.Tile);

                        // Positive gap = NPC has passed more waypoints = NPC is ahead.
                        int wpGap = r.WaypointIndex - playerWpIdx;
                        // Normalise by route length so the ramp is proportional regardless of route size.
                        float matchMultiplier = System.Math.Clamp(1f - (float)wpGap / r.Route.Length * 3f, 0.5f, 1.75f);
                        step *= matchMultiplier;

                        float roundedMultiplier = (float)System.Math.Round(matchMultiplier, 2);
                        if (System.Math.Abs(roundedMultiplier - r.LastMatchMultiplier) >= 0.01f)
                        {
                            string direction = matchMultiplier < 1f ? "slowing down" : "speeding up";
                            string trackMode = r.MatchLeader ? "leader" : "nearest player";
                            Logger.LogVerbose(
                                $"[Match AI] {r.Rider?.Name ?? r.Horse.Name}: {direction} to {roundedMultiplier:F2}x " +
                                $"(NPC waypoint {r.WaypointIndex}, player nearest waypoint {playerWpIdx}/{r.Route.Length - 1}, " +
                                $"tracking {trackMode} {targetFarmer.Name} {targetTileDist:F1} tiles away)");
                            r.LastMatchMultiplier = roundedMultiplier;
                        }
                    }
                    else if (r.LastMatchMultiplier != 1f)
                    {
                        Logger.LogVerbose($"[Match AI] {r.Rider?.Name ?? r.Horse.Name}: back to normal speed (target player {targetTileDist:F1} tiles away)");
                        r.LastMatchMultiplier = 1f;
                    }
                }

                float moved = 0f;
                while (step > 0f && r.PathIndex < r.ComputedPath.Count)
                {
                    Vector2 target = TileToPixels(r.ComputedPath[r.PathIndex]);
                    Vector2 diff = target - r.Horse.Position;
                    float dist = diff.Length();
                    if (dist < 0.5f) { r.PathIndex++; continue; }

                    if (step >= dist)
                    {
                        r.Horse.Position = target;
                        r.Horse.faceDirection(GetFacingDirection(diff));
                        moved += dist;
                        step -= dist;
                        r.PathIndex++;
                    }
                    else
                    {
                        diff.Normalize();
                        r.Horse.Position += diff * step;
                        r.Horse.faceDirection(GetFacingDirection(diff));
                        moved += step;
                        step = 0f;
                    }
                }

                // Play hoof sounds proportional to distance traveled (one beat per tile).
                if (moved > 0f)
                {
                    r.HoofSoundTimer -= moved;
                    if (r.HoofSoundTimer <= 0f)
                    {
                        r.HoofSoundTimer += 64f; // one beat per tile
                        // Same surface-dependent cue the player hears (was a flat "thudStep").
                        PlayHoofstep(loc, r.Horse.Tile);
                    }
                }

                // Re-apply gallop animation on direction change or if horse.update() reset it.
                int dir = r.Horse.FacingDirection;
                if (dir != r.LastAnimDir || !IsGalloppingAnimation(r.Horse, dir))
                {
                    r.LastAnimDir = dir;
                    SetGalloppingAnimation(r.Horse, dir);
                }

                if (r.Rider != null)
                    SyncRiderToHorse(r.Rider, r.Horse);

                // Finish detection: record the crossing but keep moving; the route ends past the line.
                if (!r.Finished)
                {
                    Vector2 t = r.Horse.Tile;
                    if (t.X >= Def.FinishMin.X && t.X <= Def.FinishMax.X
                        && t.Y >= Def.FinishMin.Y && t.Y <= Def.FinishMax.Y)
                    {
                        r.Finished = true;
                        if (IsHost)
                            this.RecordFinish(r.FakeId);
                    }
                }
            }

        }

        /// <summary>
        /// Runs A* via PathFindController's constructor, then extracts the computed tile path into
        /// <paramref name="r"/>.ComputedPath. The controller itself is discarded; we drive
        /// position directly so we never call MovePosition (blocked during eventUp).
        /// </summary>
        private bool TryComputePathToWaypoint(NpcRacer r, GameLocation loc, Point dest)
        {
            if (PfcCtor == null || PfcPathField == null)
            {
                this.Monitor.Log("PathFindController reflection unavailable. NPC horses cannot pathfind.", LogLevel.Error);
                return false;
            }
            try
            {
                var pfc = PfcCtor.Invoke(new object?[] { r.Horse, loc, dest, -1 });
                if (pfc == null) return false;
                var stack = PfcPathField.GetValue(pfc) as Stack<Point>;
                if (stack == null || stack.Count == 0) return false;
                // Stack top = next step from current position; List preserves that order.
                r.ComputedPath = new List<Point>(stack);
                r.PathIndex = 0;
                return true;
            }
            catch (System.Exception ex)
            {
                this.Monitor.Log($"Path computation failed for {dest}: {ex.InnerException?.Message ?? ex.Message}", LogLevel.Warn);
                return false;
            }
        }

        /// <summary>Returns the index of the waypoint in <paramref name="route"/> closest to <paramref name="tile"/>.</summary>
        private static int NearestWaypointIndex(Point[] route, Vector2 tile)
        {
            int idx = 0;
            float minDist = float.MaxValue;
            for (int i = 0; i < route.Length; i++)
            {
                float d = Vector2.Distance(tile, new Vector2(route[i].X, route[i].Y));
                if (d < minDist) { minDist = d; idx = i; }
            }
            return idx;
        }

        // NPC Speed
        private static float ComputeNpcSpeedPixelsPerMs(NpcRacer r)
        {
            float tilesPerSec = 5f + (r.TotalSpeed / 20);
            // Same additive bonus the player gets, so NPCs speed up equally. See HorseStats.SprintSpeedBonus.
            if (r.NpcSprintPhase == SprintPhase.Sprinting)
                tilesPerSec += HorseStats.SprintSpeedBonus(r.TotalSprint);
            // Same ground the player feels, so mud and grass read the same way for everyone.
            tilesPerSec = System.Math.Max(MinRaceSpeed, tilesPerSec + GoingSpeedBonusAt(r.Horse.Tile));
            return tilesPerSec * 64f / 1000f;
        }

        private static int GetFacingDirection(Vector2 moveDir)
        {
            if (System.Math.Abs(moveDir.X) > System.Math.Abs(moveDir.Y))
                return moveDir.X > 0 ? Game1.right : Game1.left;
            return moveDir.Y > 0 ? Game1.down : Game1.up;
        }

        private void UpdateNpcSprint(NpcRacer r, int deltaMs, int npcIndex)
        {
            if (r.NpcSprintPhase == SprintPhase.Sprinting || r.NpcSprintPhase == SprintPhase.Exhausted)
            {
                r.NpcSprintTimer -= deltaMs;
                if (r.NpcSprintTimer > 0f) return;

                if (r.NpcSprintPhase == SprintPhase.Sprinting)
                {
                    r.NpcSprintPhase = SprintPhase.Exhausted;
                    r.NpcSprintTimer = SprintCooldownMs;
                }
                else
                {
                    r.NpcSprintPhase = SprintPhase.Ready;
                    r.NpcSprintTimer = 0f;
                }
                return;
            }

            // Only the host decides when to sprint; farmhands wait for the broadcast.
            if (!IsHost) return;

            r.NextSprintCheckMs -= deltaMs;
            if (r.NextSprintCheckMs > 0f) return;

            if (Game1.random.NextDouble() < 0.5)
            {
                float durationMs = HorseStats.SprintDurationMs(r.TotalSprint);
                Logger.LogVerbose($"Sprint (NPC {r.Rider?.Name ?? "?"}): sprint={r.TotalSprint}, duration={durationMs}ms, speed=+{HorseStats.SprintSpeedBonus(r.TotalSprint)}");
                r.NpcSprintPhase = SprintPhase.Sprinting;
                r.NpcSprintTimer = durationMs;
                if (Game1.IsMultiplayer)
                    this.Helper.Multiplayer.SendMessage(
                        new NpcSprintMessage(npcIndex, durationMs),
                        MsgNpcSprint,
                        modIDs: new[] { this.Helper.ModRegistry.ModID });
            }
            r.NextSprintCheckMs = (float)(Game1.random.NextDouble() * 8000.0 + 3000.0);
        }


        /// <summary>
        /// Moves a rider NPC to the horse's position + <see cref="RiderOffset"/> and locks their
        /// sprite to the neutral standing frame that matches the horse's facing direction.
        /// Standard NPC spritesheet layout: down=0, right=4, up=8, left=12.
        /// </summary>
        private static void SyncRiderToHorse(NPC rider, Horse horse)
        {
            rider.Position = horse.Position + RiderOffset;
            rider.faceDirection(horse.FacingDirection);
        }

        /// <summary>Direction everyone faces once they're placed for the awards ceremony: up, toward
        /// the host at the announcer tile.</summary>
        private const int CeremonyFacing = Game1.up;

        /// <summary>Points a rider and their mount in a fixed direction when they're teleported into the
        /// winner's circle.
        ///
        /// faceDirection alone does not do it: AnimatedSprite.faceDirection bails out while
        /// CurrentAnimation is set, and everyone arrives still carrying the gallop loop from whatever
        /// direction they finished in, which AdvanceHorseAnimations keeps stepping, so the horse and its
        /// rider stay visibly turned sideways on the podium. Swap in the standing frame for the target
        /// direction instead.</summary>
        private static void FaceForCeremony(Farmer? rider, Horse? horse, int direction)
        {
            // Rider first: Farmer.Halt (reached through completelyStopAnimatingOrDoingAction) nulls its
            // mount's CurrentAnimation, so the horse has to be posed after the farmer is settled.
            if (rider != null)
            {
                rider.completelyStopAnimatingOrDoingAction();
                rider.faceDirection(direction);
                // Mounted farmers draw a dedicated riding frame per direction, picked at call time.
                if (rider.isRidingHorse())
                    rider.showRiding();
            }

            if (horse != null)
            {
                horse.Halt();
                horse.faceDirection(direction);
                HorseAnimations.SetStanding(horse, direction);
            }
        }

        /// <summary>Re-asserts the ceremony pose every tick for everyone standing in the winner's circle.
        ///
        /// Posing once at placement does not hold: Farmer.Halt clears its mount's CurrentAnimation (and
        /// StartCeremony itself calls Halt after placing everyone), after which EnsureLocalMountAnimation
        /// tops the horse back up with the side-on idle, so the rider ends up sideways on the podium.
        /// Runs after AdvanceHorseAnimations so this is the last word on the frame each tick, and only
        /// touches what is actually wrong so it costs nothing once everyone is settled.</summary>
        private void EnforceCeremonyFacing()
        {
            // Everyone at the festival raced, so everyone is posed, including anyone missing from
            // ceremonyOrder (a player who never recorded a finish still stands with the rest).
            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                if (farmer.FacingDirection != CeremonyFacing)
                    farmer.faceDirection(CeremonyFacing);
                if (farmer.isRidingHorse())
                    farmer.showRiding();
                PoseHorseForCeremony(farmer.mount);
            }

            foreach (NpcRacer racer in npcRacers)
            {
                PoseHorseForCeremony(racer.Horse);
                if (racer.Rider != null && racer.Rider.FacingDirection != CeremonyFacing)
                {
                    racer.Rider.Sprite?.StopAnimation();
                    racer.Rider.faceDirection(CeremonyFacing);
                }
            }
        }

        private static void PoseHorseForCeremony(Horse? horse)
        {
            if (horse == null)
                return;
            if (horse.FacingDirection != CeremonyFacing)
                horse.faceDirection(CeremonyFacing);
            if (!HorseAnimations.IsStanding(horse, CeremonyFacing))
                HorseAnimations.SetStanding(horse, CeremonyFacing);
        }

        private static bool IsGalloppingAnimation(Horse horse, int dir)
        {
            var anim = horse.Sprite?.CurrentAnimation;
            if (anim == null || anim.Count == 0) return false;
            int expectedFirst = dir switch { 0 => 15, 1 or 3 => 8, _ => 1 };
            return anim[0].frame == expectedFirst;
        }

        /// <summary>
        /// Sets the horse's sprite to the vanilla mounted-gallop animation for the given direction.
        /// Frames sourced from Horse.update() in vanilla 1.6: right/left use 8–13, up uses 15–20,
        /// down uses 1–6, each at 70 ms. Only reassigns when direction actually changes.
        /// </summary>
        private static void SetGalloppingAnimation(Horse horse, int dir)
        {
            bool flip = dir == Game1.left;
            List<FarmerSprite.AnimationFrame> frames = dir switch
            {
                // up
                0 => new List<FarmerSprite.AnimationFrame>
                {
                    new(15, 70), new(16, 70), new(17, 70),
                    new(18, 70), new(19, 70), new(20, 70),
                },
                // right or left (same frames, left uses flip)
                1 or 3 => new List<FarmerSprite.AnimationFrame>
                {
                    new(8,  70, false, flip), new(9,  70, false, flip),
                    new(10, 70, false, flip), new(11, 70, false, flip),
                    new(12, 70, false, flip), new(13, 70, false, flip),
                },
                // down
                _ => new List<FarmerSprite.AnimationFrame>
                {
                    new(1, 70), new(2, 70), new(3, 70),
                    new(4, 70), new(5, 70), new(6, 70),
                },
            };
            horse.Sprite.loop = true;
            horse.Sprite.setCurrentAnimation(frames);
        }

        // ======================== Betting System ========================

        internal const string BetRewardQuestId = "HorseTycoon.BetReward";
        internal const string BetRewardAwayQuestId = "HorseTycoon.BetRewardAway";

        private void DeliverBetResult()
        {
            bool won = this.CheckBetWon();
            string? winnerName = this.GetBetWinnerName();
            string betName = betTargetNpcName.Value
                ?? (betTargetFarmerId.Value.HasValue
                    ? Game1.getAllFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == betTargetFarmerId.Value.Value)?.Name ?? "Unknown"
                    : "Unknown");

            if (!won)
            {
                Logger.LogVerbose($"Bet lost for {Game1.player.Name}: picked {betName}, winner was {winnerName}.");
                return;
            }

            // Odds bets (the Bouncer's away-festival book) pay stake + stake × odds; Pam's
            // walk-in book is a flat winner-takes-double.
            bool oddsBet = betOddsDenominator.Value > 0;
            int winnings = oddsBet
                ? BookiePayout(betAmount.Value, betOddsNumerator.Value, betOddsDenominator.Value)
                : betAmount.Value * 2;
            string bookieName = oddsBet ? "the Bouncer" : "Pam";
            string oddsNote = oddsBet ? $" at {betOddsNumerator.Value}/{betOddsDenominator.Value}" : "";
            var quest = new Quest();
            quest.id.Value = oddsBet ? BetRewardAwayQuestId : BetRewardQuestId;
            quest.questType.Value = Quest.type_basic;
            quest.questTitle = "Horse Race Bet";
            quest.questDescription = $"You called it! {winnerName} took the top spot{oddsNote}. Go collect your winnings from {bookieName}.";
            quest.currentObjective = "Collect your winnings.";
            quest.moneyReward.Value = winnings;
            quest.completed.Value = true;
            quest.accepted.Value = true;
            quest.showNew.Value = true;
            Game1.player.questLog.Add(quest);

            Logger.LogVerbose($"Bet won for {Game1.player.Name}: {winnings}g quest added to quest log.");
        }

        private bool CheckBetWon()
        {
            long localId = Game1.player.UniqueMultiplayerID;
            foreach (long id in ceremonyOrder.Value)
            {
                if (id == localId) continue;
                if (betTargetFarmerId.Value.HasValue)
                    return id == betTargetFarmerId.Value.Value;
                if (betTargetNpcName.Value != null && id < 0)
                {
                    NpcRacer? racer = npcRacers.Find(r => r.FakeId == id);
                    return racer?.Rider?.Name == betTargetNpcName.Value;
                }
                return false;
            }
            return false;
        }

        private string? GetBetWinnerName()
        {
            long localId = Game1.player.UniqueMultiplayerID;
            foreach (long id in ceremonyOrder.Value)
            {
                if (id == localId) continue;
                if (id < 0)
                {
                    NpcRacer? racer = npcRacers.Find(r => r.FakeId == id);
                    return racer?.Rider?.displayName ?? racer?.Rider?.Name;
                }
                Farmer? farmer = Game1.getAllFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == id);
                return farmer?.Name;
            }
            return null;
        }

        // ======================== End Betting System ========================

        // ======================== End NPC Racer System ========================

        private void RestartRace()
        {
            if (RaceFestival == null)
            {
                this.Monitor.Log("ht_race_restart: not currently in the horse festival.", LogLevel.Warn);
                return;
            }

            // Capture competitor and def before Reset() clears them.
            Horse? horse = competitor.Value ?? Game1.player.mount;
            FestivalDefinition? def = activeDef.Value;

            this.Reset(); // removes stalls/NPC racers, sets phase = None

            // Fully undo LineUp()'s player state: dismount, clear jump physics, restore movement.
            if (Game1.player.mount != null)
                Game1.player.mount.rider = null;
            Game1.player.mount = null;
            Game1.player.freezePause = 0;
            Game1.player.yJumpVelocity = 0f;
            Game1.player.yJumpOffset = 0;
            Game1.player.CanMove = true;
            Game1.player.Halt();
            Game1.player.completelyStopAnimatingOrDoingAction();

            // Return their horse to the pasture and seed lastRiddenMount so EnterPasture
            // (called on the next Phase.None tick) picks it up automatically.
            // Restore activeDef temporarily so PlaceHorseInPasture/PastureSpawnForSlot can read Def.
            if (horse != null && def != null)
            {
                activeDef.Value = def;
                int slot = PastureSlotFor(Game1.player);
                PlaceHorseInPasture(horse, slot);
                activeDef.Value = null;
                Game1.player.Position = TileToPixels(new Point(def.StarterStartTile.X, def.StarterStartTile.Y + 1));
                Game1.player.faceDirection(Game1.up);
                lastRiddenMount.Value = horse;
                lastMountedTick.Value = Game1.ticks;
            }

            if (def != null)
            {
                NPC? starter = RaceFestival?.getActorByName(def.StarterName);
                if (starter != null)
                {
                    starter.Position = TileToPixels(def.StarterStartTile);
                    starter.faceDirection(Game1.down);
                    starter.Halt();
                }
            }

            this.Monitor.Log("ht_race_restart: reset to pasture phase.", LogLevel.Info);
        }

        private void Reset()
        {
            this.RestoreSuppressedBuffs();
            this.RemoveStartingStalls();
            this.startCountdown.Value = -1f;
            this.raceMusicStarted.Value = false;
            sprintPhase.Value = SprintPhase.Ready;
            sprintTimer.Value = 0f;
            warriorEnergyTimer.Value = 0f;
            warriorEnergyBonus.Value = 0f;
            lastWarriorEnergyTile.Value = -Vector2.One;
            if (penHorse.Value != null)
            {
                penHorse.Value.currentLocation?.characters.Remove(penHorse.Value);
                penHorse.Value = null;
            }
            if (jasOnHorse.Value != null)
            {
                jasOnHorse.Value.EventActor = false;
                jasOnHorse.Value = null;
            }
            if (pastureAnimal.Value != null)
            {
                pastureAnimal.Value.currentLocation?.animals.Remove(pastureAnimal.Value.myID.Value);
                pastureAnimal.Value = null;
            }
            if (borrowedFestivalHorse.Value != null)
            {
                borrowedFestivalHorse.Value.currentLocation?.characters.Remove(borrowedFestivalHorse.Value);
                borrowedFestivalHorse.Value = null;
            }
            this.StowBusHorses();
            // Catch-all: if anything did respawn a blank stable Horse while we were away, rebuild its
            // appearance from the barn horse now rather than leaving it vanilla until the next day start.
            if (Context.IsMainPlayer)
                HorseHelper.SyncStableHorseAppearance();
            // Release this player's bus claims (locally and on every client) so the horses can board
            // a later bus today now that they're back home.
            if (SummerBusHorseIds.Count > 0)
            {
                foreach (long animalId in SummerBusHorseIds)
                {
                    if (BusHorseClaims.TryGetValue(animalId, out long owner) && owner == Game1.player.UniqueMultiplayerID)
                        BusHorseClaims.Remove(animalId);
                }
                if (Game1.IsMultiplayer)
                {
                    this.Helper.Multiplayer.SendMessage(
                        new BusClaimMessage(new List<long>(SummerBusHorseIds), Game1.player.UniqueMultiplayerID, Release: true),
                        MsgBusClaim,
                        modIDs: new[] { this.Helper.ModRegistry.ModID });
                }
            }
            SummerBusSelectionMade = false;
            SummerBusHorseIds.Clear();
            phase.Value = Phase.None;
            // Bus drive-in cinematic cleanup (safety net if the festival ended mid-arrival).
            this.UnparkBus();
            busDoorSprite.Value = null;
            busArrivalDoorTimer.Value = -1f;
            busArrivalMotion.Value = Vector2.Zero;
            Game1.displayFarmer = true;
            Game1.viewportFreeze = false;
            activeDef.Value = null;
            shuffledPenSlots.Value = null;
            competitor.Value = null;
            this.SetLayerVisible("Racing", false);
            this.SetLayerVisible("AwardsEvent", false);
            this.DespawnSpectators();
            this.RemoveDesertTrader();
            setupSpectators = null;
            racingSpectators = null;
            ceremonySpectators = null;
            wanderMoving.Value = false;
            wanderDir.Value = -1;
            wanderTicks.Value = 0;
            readyCheckOpen.Value = false;
            pendingRaceReadyCheckId.Value = null;
            goBarrierId.Value = null;
            goBarrierOpened.Value = false;
            announcementShown.Value = false;
            ceremonyOrder.Value.Clear();
            ceremonyStep.Value = 0;
            ceremonyEndBarrierId.Value = null;
            ceremonyEnded.Value = false;
            prizeMenuWasOpen = false;
            suppressFadeTicks = 0;
            disqualified.Value = false;
            pamGreeted.Value = false;
            starterGreeted.Value = false;
            showBettingMoneyBox.Value = false;
            betTargetFarmerId.Value = null;
            betTargetNpcName.Value = null;
            betAmount.Value = 0;
            betOddsNumerator.Value = 0;
            betOddsDenominator.Value = 0;
            postedOdds.Value = null;
            // Static host-side state, safe to clear on any screen since only the host writes to these.
            FinishOrder.Clear();
            DisqualifiedFarmers.Clear();
            HostCeremonyCountdown = -1f;
            Game1.viewportFreeze = false;

            this.DespawnPenNpcHorses();
            this.DespawnDecorativeHorses();

            // NPC racer cleanup: remove AI horses; restore rider NPCs to their original locations.
            foreach (NpcRacer r in npcRacers)
            {
                r.Horse.controller = null;
                r.Horse.currentLocation?.characters.Remove(r.Horse);
            }
            npcRacers.Clear();
            goingByTile.Clear();
            this.hoofSoundTimer.Value = 0f;
            this.lastHoofPos.Value = null;
            npcRacersSpawned = false;
            nextNpcFakeId = -1L;
            this.DespawnNpcRiders(); // safety net if EndFestival wasn't reached
        }

        private static Vector2 TileToPixels(Point tile) => new(tile.X * 64f, tile.Y * 64f);
    }

    /// <summary>A starting-stall gate with no collision footprint while open.
    ///
    /// Event.checkForCollision blocks on every object's bounding box without calling isPassable(), so a
    /// vanilla open gate still blocks the rider inside the festival. We override the box to empty while open;
    /// when closed it behaves like a normal gate. These gates only live in the festival's temporary map and
    /// are never saved or net-synced.</summary>
    public class RaceStartGate : Fence
    {
        public RaceStartGate() : base(Vector2.Zero, "325", isGate: true) { }

        public RaceStartGate(Vector2 tile, string fenceId = "325") : base(tile, fenceId, isGate: true) { }

        public override Rectangle GetBoundingBoxAt(int x, int y) =>
            this.gatePosition.Value >= 88 ? Rectangle.Empty : base.GetBoundingBoxAt(x, y);

        // PathFindController A* uses isTileOccupied → obj.isPassable() to decide walkability.
        // Without this override the open gate still blocks NPC horse pathfinding.
        public override bool isPassable() => this.gatePosition.Value >= 88;
    }
}
