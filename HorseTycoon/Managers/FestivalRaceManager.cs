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
using StardewValley.Characters;
using StardewValley.Menus;
using StardewValley.Objects;

namespace HorseTycoon
{
    /// <summary>
    /// Drives the Spring 21 Horse Festival race in Cindersap Forest.
    /// The host starts the race by talking to Lewis, which raises a <see cref="ReadyCheckDialog"/> on every
    /// client; once everyone accepts, each player is lined up in a fenced starting stall, a start sound plays,
    /// and when it finishes the gates open. Horses run east to the finish band, which ends the race.
    /// Local state is PerScreen; multiplayer sync uses Game1.netReady and explicit mod messages.
    /// </summary>
    public class FestivalRaceManager
    {
        // Festival event id format: "festival_" + the file key from Event.tryToLoadFestival.
        private const string FestivalEventId = "festival_spring21";
        private const string ReadyCheckName = "Froshty.HorseTycoon.horseRaceStart";

        private const float SprintCooldownMs = 10000f;
        private enum SprintPhase { Ready, Sprinting, Exhausted }

        // --- Tunable map coordinates (tiles) for CP.HorseTycoon_ForestFestival. Tune in-game with `ht_race_tile`. ---
        private static readonly Point PastureMin = new(80, 18);
        private static readonly Point PastureMax = new(90, 23);
        private static readonly Point PastureSpawn = new(98, 20);
        // Clockwise slot offsets (tiles) from PastureSpawn, one per player.
        private static readonly Point[] PastureSlotOffsets =
        {
            new(0, 0), new(-4, 0), new(0, -4), new(4, 0), new(0, 4),
        };
        // Stall i's horse tile is (StartStall.X, StartStall.Y + i); horses break east into the course.
        private static readonly Point StartStall = new(39, 48);
        // Finish band (inclusive tile rectangle).
        private static readonly Point FinishMin = new(40, 11);
        private static readonly Point FinishMax = new(40, 17);
        // Disqualification zone: area north of the starting-gate's north fence AND east of the finish-line's east barrier.
        // A player who enters this zone while racing has jumped off the track and is disqualified.
        // Tune with `ht_race_tile`.
        private static readonly int DqZoneNorthOfY = 43;  // player.Tile.Y < this value
        private static readonly int DqZoneEastOfX = 41;   // player.Tile.X > this value
        // Where a DQ'd player (and their horse) is teleported — just past the finish line in the spectator area.
        private static readonly Point DqArrivalTile = new(44, 14);
        // Decorative pony-ride horse in the pen to the left of Leah's house.
        private static readonly Point PenHorseTile = new(94, 31);
        private static readonly string[] AllSkins = { "Roan", "BlueRoan", "Dapple", "Bay", "Belgian", "Shire", "Chestnut" };
        private static readonly string[] MarnieHorseNames = {
            "Clover", "Daisy", "Biscuit", "Big Red", "Rosie", "Pepper",
            "Nutmeg", "Ember", "Cobalt", "Juniper", "Bramble", "Dusty",
        };

        private const int WanderRepickMinTicks = 60;
        private const int WanderRepickMaxTicks = 180;

        // NPC racers: Marnie, Leah, Abigail ride in the race as AI opponents.
        // Speed stat drives tiles/sec via 5 + (speed / 20): 20 → 6 t/s, 40 → 7 t/s, 60 → 8 t/s.
        private static readonly string[] NpcRiderNames = { "Marnie", "Leah", "Abigail", "Sebastian" };
        private static readonly int[] NpcRiderSpeeds = { 15, 25, 35, 40 };
        private static readonly int[] NpcRiderSprints = { 20, 35, 45, 45 };


        // Per-NPC race routes. Each NPC is assigned a different route by index.
        // Add more routes here and each new NPC slot will pick the next one (cycling if needed).
        private static readonly Point[][] NpcRaceRoutes =
        {
            // Route 0 (Marnie)
            new Point[]
            {
                new(49, 49),
                new(58, 48),
                new(74, 49),
                new(85, 51),
                new(88, 60),
                new(89, 66),
                new(91, 70),
                new(91, 75),
                new(91, 80),
                new(87, 85),
                new(74, 82),
                new(73, 74),
                new(72, 70),
                new(68, 70),
                new(59, 72),
                new(52, 74),
                new(45, 77),
                new(38, 84),
                new(37, 89),
                new(38, 95),
                new(31, 97),
                new(23, 94),
                new(20, 87),
                new(19, 84),
                new(19, 78),
                new(20, 74),
                new(20, 66),
                new(19, 62),
                new(20, 59),
                new(21, 54),
                new(24, 47),
                new(25, 41),
                new(21, 37),
                new(18, 33),
                new(20, 22),
                new(23, 18),
                new(27, 16),
                new(35, 16),
                new(43, 16), // past the finish
            },
            // Route 1 (Leah)
            new Point[]
            {
                new(49, 45),
                new(58, 45),
                new(69, 45),
                new(76, 49),
                new(87, 51),
                new(88, 61),
                new(91, 73),
                new(88, 81),
                new(82, 84),
                new(76, 79),
                new(69, 70),
                new(57, 75),
                new(45, 77),
                new(38, 84),
                new(38, 95),
                new(27, 93),
                new(28, 88),
                new(24, 85),
                new(19, 79),
                new(18, 62),
                new(22, 50),
                new(20, 41),
                new(18, 36),
                new(21, 20),
                new(33, 14),
                new(44, 13), // past the finish
            },
            // Route 2 (Abigail)

            new Point[]
            {
                new(54, 49),
                new(68, 49),
                new(75, 50),
                new(85, 50),
                new(88, 63),
                new(92, 73),
                new(87, 80),
                new(86, 85),
                new(73, 79),
                new(69, 72),
                new(60, 71),
                new(54, 77),
                new(46, 79),
                new(41, 84),
                new(37, 89),
                new(40, 93),
                new(39, 96),
                new(31, 97),
                new(22, 92),
                new(18, 83),
                new(17, 75),
                new(17, 63),
                new(17, 55),
                new(17, 48),
                new(16, 44),
                new(18, 39),
                new(18, 35),
                new(19, 25),
                new(24, 17),
                new(33, 14),
                new(37, 14),
                new(46, 14), // past the finish
            },
            // Route 3 (Sebastian)
            new Point[]
            {
                new(52, 47),
                new(58, 48),
                new(65, 48),
                new(72, 48),
                new(84, 49),
                new(88, 58),
                new(89, 63),
                new(91, 68),
                new(91, 72),
                new(90, 76),
                new(89, 81),
                new(85, 85),
                new(79, 84),
                new(75, 82),
                new(72, 76),
                new(70, 72),
                new(59, 70),
                new(49, 75),
                new(41, 78),
                new(38, 84),
                new(38, 89),
                new(38, 96),
                new(32, 97),
                new(23, 93),
                new(19, 85),
                new(18, 77),
                new(18, 68),
                new(17, 59),
                new(22, 53),
                new(25, 46),
                new(21, 42),
                new(18, 35),
                new(18, 25),
                new(22, 20),
                new(27, 15),
                new(37, 15),
                new(47, 13), // past the finish
            },
        };

        // Pixel offset applied to each rider NPC so they appear seated on the horse.
        // Negative Y moves the sprite visually upward on screen; tune if the rider looks off.
        private static readonly Vector2 RiderOffset = new(-12f, -40f);

        // The game dismounts the player as they warp into the festival, so we capture the mount each tick
        // beforehand and treat them as "arrived mounted" if they were riding within this window.
        private const int EntryMountWindowTicks = 600;

        // Winner's circle tiles (1st, 2nd, 3rd place left-to-right), and Lewis's announcer position.
        // Tune in-game with `ht_race_tile`.
        private static readonly Point[] WinnersCircleTiles =
        {
            new(58, 12), // 1st place
            new(56, 12), // 2nd place
            new(54, 12), // 3rd place
        };
        private static readonly Point LewisAnnouncerTile = new(56, 9);
        // Spectator tiles for players/NPCs who didn't make the podium, spread east-to-west south of the winners circle.
        private static readonly Point[] SpectatorTiles =
        {
            new(56, 15),
            new(54, 15),
            new(58, 15),
            new(52, 15),
            new(60, 15),
        };

        // Delay between the last player crossing the finish line and the ceremony starting.
        private const float CeremonyDelayMs = 2000f;
        // Hard cap on total racers (players + NPCs). NPC slots are filled first-come, first-dropped.
        private const int MaxRacers = 8;

        private enum Phase { None, Pasture, Racing, Finished, Ceremony }
        private enum AiMode { Normal, Match }

        private class NpcRacer
        {
            public Horse Horse = null!;
            public NPC? Rider;
            public long FakeId;
            public int TotalSpeed;
            public int TotalSprint;
            public Point[] Route = NpcRaceRoutes[0];
            public int WaypointIndex;
            public bool Finished;
            public SprintPhase NpcSprintPhase = SprintPhase.Ready;
            public float NpcSprintTimer;
            public float NextSprintCheckMs;
            public int LastAnimDir = -1;
            // A*-computed tile path to the current waypoint; driven by direct position updates.
            public List<Point> ComputedPath = new();
            public int PathIndex;
            public float HoofSoundTimer;
            public bool MovementDone;
            public AiMode AiMode = AiMode.Match;
            // When true, this NPC tracks the race leader instead of the nearest farmer.
            public bool MatchLeader;
            // Last multiplier applied by match AI; used to suppress redundant log lines.
            public float LastMatchMultiplier = 1f;
        }

        private static readonly bool VerboseLogging = true;

        private readonly IModHelper Helper;
        private readonly IMonitor Monitor;
        private Texture2D? sprintBuffIcon;

        private void LogVerbose(string message)
        {
            if (VerboseLogging)
                this.Monitor.Log(message, LogLevel.Debug);
        }

        private readonly PerScreen<Phase> phase = new(() => Phase.None);
        private readonly PerScreen<Horse?> competitor = new(() => null);
        private readonly PerScreen<FarmAnimal?> pastureAnimal = new(() => null);
        private readonly PerScreen<Horse?> lastRiddenMount = new(() => null);
        private readonly PerScreen<int> lastMountedTick = new(() => int.MinValue);
        private readonly PerScreen<Vector2> wanderTarget = new(() => Vector2.Zero);
        private readonly PerScreen<bool> wanderMoving = new(() => false);
        private readonly PerScreen<int> wanderDir = new(() => -1);
        private readonly PerScreen<int> wanderTicks = new(() => 0);
        private readonly PerScreen<bool> readyCheckOpen = new(() => false);
        private readonly PerScreen<List<Vector2>> stallFenceTiles = new(() => new List<Vector2>());
        private readonly PerScreen<bool> stallsSpawned = new(() => false);
        private readonly PerScreen<Horse?> penHorse = new(() => null);
        private readonly PerScreen<NPC?> jasOnHorse = new(() => null);
        private readonly PerScreen<Horse?> borrowedFestivalHorse = new(() => null);
        // -1 means inactive. Kept in real time so it ticks while the festival pauses the game clock.
        private readonly PerScreen<float> startCountdown = new(() => -1f);
        private readonly PerScreen<List<Buff>> suppressedBuffs = new(() => new List<Buff>());
        private readonly PerScreen<SprintPhase> sprintPhase = new(() => SprintPhase.Ready);
        private readonly PerScreen<float> sprintTimer = new(() => 0f);
        private readonly PerScreen<System.TimeSpan> raceStartTime = new(() => System.TimeSpan.Zero);
        private readonly PerScreen<bool> disqualified = new(() => false);

        // Set true while re-invoking warpFarmer from the "Yes" path so our prefix doesn't re-intercept it.
        private static bool SkipHorseWarning = false;

        private readonly PerScreen<List<long>> ceremonyOrder = new(() => new List<long>());
        private readonly PerScreen<int> ceremonyStep = new(() => 0);

        // NPC racers are the same object list for every screen; spawned once per race via guard flags.
        private readonly List<NpcRacer> npcRacers = new();
        // Tracks NPCs borrowed from their world locations so they can be restored on festival end.
        private readonly List<NPC> spawnedRiders = new();
        private bool npcRidersBorrowed = false;
        private bool npcRacersSpawned = false;
        private long nextNpcFakeId = -1L;

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
        // at 144 — past the vanilla Characters sheet's 144 tiles — making loadActors ignore them safely.
        private const int SveCharacterSheetPadding = 144;
        private const int EsCharacterSheetPadding  = 144;

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
        private const string MsgOpenReadyCheck = "OpenReadyCheck";
        private const string MsgPlayerFinished = "PlayerFinished";
        private const string MsgPlayerDisqualified = "PlayerDisqualified";
        private const string MsgStartCeremony = "StartCeremony";
        private record StartCeremonyMessage(List<long> RankedPlayerIds);
        private const string MsgNpcSprint = "NpcSprint";
        private record NpcSprintMessage(int NpcIndex, float DurationMs);

        // Custom start sound registered as a Data/AudioChanges cue in [CP] HorseTycoon/data/sound.json.
        private const string RaceStartSoundCue = "CP.HorseTycoon_RaceStart";
        private const int RaceStartSoundMs = 8000;

        public void Initialize()
        {
            sprintBuffIcon = this.Helper.ModContent.Load<Texture2D>("assets/HorseRunningBuff.png");
            this.Helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            this.Helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            this.Helper.Events.Display.RenderedHud += this.OnRenderedHud;
            this.Helper.Events.Multiplayer.ModMessageReceived += this.OnMessageReceived;

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
            // inside that overload — so this prefix fires before any of that logic runs.
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), "warpFarmer",
                    new[] { typeof(LocationRequest), typeof(int), typeof(int), typeof(int) }),
                prefix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(WarpFarmer_Prefix)));

            this.Helper.ConsoleCommands.Add(
                "ht_race_tile",
                "Logs the player's current tile (for tuning festival race coordinates).",
                (_, _) => this.Monitor.Log(
                    $"Player tile: {Game1.player.Tile} | mounted: {Game1.player.isRidingHorse()} | location: {Game1.currentLocation?.Name}",
                    LogLevel.Info));

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

        /// <summary>Used by the getMovementSpeed transpiler: report "not in an event" during our race.</summary>
        public static bool EventUpForSpeed() => Game1.eventUp && !RaceRidingActive;

        /// <summary>True whenever the horse festival is in any of the racing phases).</summary>
        public static bool IsInAnyRacingPhase => Instance?.phase.Value is Phase.Racing or Phase.Finished;

        private static void GetMovementSpeed_Postfix(ref float __result)
        {
            if (IsInAnyRacingPhase)
                __result *= 0.75f;
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

        /// <summary>The active Horse Festival event, or null if we're not in it.</summary>
        private static Event? RaceFestival
        {
            get
            {
                Event? ev = Game1.currentLocation?.currentEvent;
                return ev != null && ev.isFestival && ev.id == FestivalEventId ? ev : null;
            }
        }

        // ======================== Festival Entry Horse Warning ========================

        /// <summary>
        /// Fires before Game1.warpFarmer(LocationRequest, int, int, int) — the single overload that all
        /// string convenience warpFarmer overloads delegate into, and where the festival attendance gate
        /// (SP dialog / MP ReadyCheckDialog "festivalStart") lives. Cancels the warp to Forest during the
        /// festival window if the player is not mounted and has a rideable horse on the farm, then shows
        /// a yes/no confirmation. "Yes" re-invokes warpFarmer with a bypass flag; "No" does nothing.
        /// </summary>
        private static bool WarpFarmer_Prefix(LocationRequest locationRequest, int tileX, int tileY, int facingDirectionAfterWarp)
        {
            if (SkipHorseWarning) return true;
            if (locationRequest?.Name != "Forest") return true;
            if (!IsFestivalTimeWindow()) return true;
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

        private static bool IsFestivalTimeWindow()
        {
            return Game1.currentSeason == "spring"
                && Game1.dayOfMonth == 21
                && Game1.timeOfDay >= 1200
                && Game1.timeOfDay < 1800;
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

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;


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
                return;

            switch (phase.Value)
            {
                case Phase.None:
                    this.EnterPasture(festival);
                    break;
                case Phase.Pasture:
                    this.UpdatePasture();
                    AdvanceHorseAnimations();
                    break;
                case Phase.Racing:
                    this.UpdateStartCountdown();
                    // animateOnce is gated on !Game1.eventUp, so we advance all horses ourselves.
                    AdvanceHorseAnimations();
                    // Check player finish first so the player wins any same-tick tie with an NPC.
                    this.CheckFinish();
                    this.CheckDisqualification();
                    this.UpdateNpcRacers();
                    break;
                case Phase.Finished:
                    AdvanceHorseAnimations();
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
                    this.UpdateCeremony();
                    if (disqualified.Value)
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
            float durationMs = System.Math.Clamp(totalSprint / 10f * 1000f, 1000f, 10000f);

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

        private static Point PastureSpawnForSlot(int slot)
        {
            Point offset = slot < PastureSlotOffsets.Length ? PastureSlotOffsets[slot] : new(slot * 4, 0);
            return new Point(PastureSpawn.X + offset.X, PastureSpawn.Y + offset.Y);
        }

        private void SetLayerVisible(string layerName, bool visible)
        {
            var layer = Game1.currentLocation?.map.GetLayer(layerName);
            if (layer != null) layer.Visible = visible;
        }

        private void EnterPasture(Event festival)
        {
            phase.Value = Phase.Pasture;
            competitor.Value = null;

            // Hide phase-specific layers regardless of what the TMX file saved.
            this.SetLayerVisible("Racing", false);
            this.SetLayerVisible("AwardsEvent", false);

            // Cache NPC placement data from the phase layers.
            setupSpectators = this.ReadNpcPlacements("Set-Up", sveOnly: true);
            racingSpectators = this.ReadNpcPlacements("Racing");
            ceremonySpectators = this.ReadNpcPlacements("AwardsEvent");

            // showWorldCharacters must be set on every client so the event renderer draws horses placed by any client.
            festival.showWorldCharacters = true;

            // Every client builds its own stalls — the festival temp map's objects aren't net-synced.
            this.SpawnStartingStalls();
            this.SpawnPenHorse();
            this.SpawnSpectators(setupSpectators);
            // NPC riders are borrowed in SpawnNpcRacers (race start) so they aren't visible during the pasture phase.


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

            this.LogVerbose($"Brought '{horse.Name}' into the festival pasture (slot {slot}).");
        }

        private void OnMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.Type == MsgOpenReadyCheck && RaceFestival != null && phase.Value == Phase.Pasture)
            {
                this.OpenRaceReadyCheck();
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
                this.StartCeremony(e.ReadAs<StartCeremonyMessage>().RankedPlayerIds);
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
                this.LogVerbose($"Placed remote borrowed horse in pasture slot {bmsg.Slot}.");
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
            this.LogVerbose($"Placed remote horse '{horse.Name}' in pasture slot {msg.Slot}.");
        }

        /// <summary>Creates a borrowed horse and sets competitor/borrowedFestivalHorse. No pasture
        /// placement or broadcast — call AssignBorrowedHorse for the pasture-phase UI flow.</summary>
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
            borrowedFestivalHorse.Value = horse;
            competitor.Value = horse;
            this.LogVerbose($"Auto-assigned borrowed horse '{horse.Name}' to {Game1.player.Name}.");
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

            this.LogVerbose($"Placed borrowed horse '{horse.Name}' in pasture slot {slot}.");
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
            SetGrazingAnimation(horse);
        }

        private void UpdatePasture()
        {
            Horse? horse = competitor.Value;
            if (horse == null)
                return;
            if (horse.Sprite?.CurrentAnimation == null)
                SetGrazingAnimation(horse);
        }

        private static void SetIdleAnimation(Horse horse)
        {
            if (horse.Sprite == null) return;
            bool flip = horse.FacingDirection == Game1.left;
            horse.Sprite.loop = true;
            horse.Sprite.setCurrentAnimation(new List<FarmerSprite.AnimationFrame>
            {
                new FarmerSprite.AnimationFrame(7, 1000, secondaryArm: false, flip: flip),
            });
        }

        private static void SetGrazingAnimation(Horse horse)
        {
            if (horse.Sprite == null)
                return;
            bool flip = horse.FacingDirection == Game1.left;
            horse.Sprite.loop = true;
            horse.Sprite.setCurrentAnimation(new List<FarmerSprite.AnimationFrame>
            {
                new FarmerSprite.AnimationFrame(7, Game1.random.Next(1000, 3200), secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(21, 100, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(22, 100, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(23, 400, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(24, 400, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(23, 400, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(24, 400, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(23, 400, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(24, 400, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(23, 400, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(22, 100, secondaryArm: false, flip: flip),
                new FarmerSprite.AnimationFrame(21, 100, secondaryArm: false, flip: flip),
            });
        }

        private void SpawnPenHorse()
        {
            GameLocation loc = Game1.currentLocation;
            var horse = new Horse(System.Guid.NewGuid(), PenHorseTile.X, PenHorseTile.Y);
            horse.Name = "PenHorse";
            horse.modData[HorseHelper.HorseSkinKey] = AllSkins[Game1.random.Next(AllSkins.Length)];
            horse.modData[HorseHelper.OverlaysKey] = "Saddle,Bridle";
            horse.currentLocation = loc;
            horse.Position = TileToPixels(PenHorseTile);
            horse.Halt();
            horse.faceDirection(Game1.left);
            horse.EventActor = true;
            if (!loc.characters.Contains(horse))
                loc.characters.Add(horse);
            SetGrazingAnimation(horse);
            penHorse.Value = horse;
            this.LockJasOnHorse(loc);
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

        private static void AdvanceHorseAnimations()
        {
            GameTime time = Game1.currentGameTime;
            foreach (NPC npc in Game1.currentLocation.characters)
                if (npc is Horse h && h.Sprite?.CurrentAnimation != null)
                    h.Sprite.animateOnce(time);
            foreach (Farmer farmer in Game1.getOnlineFarmers())
                if (farmer.mount?.Sprite?.CurrentAnimation != null)
                    farmer.mount.Sprite.animateOnce(time);
        }

        /// <summary>Redraw the sprint/exhausted icon during the race — the buff HUD is suppressed during events.</summary>
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (!RaceRidingActive || sprintPhase.Value == SprintPhase.Ready)
                return;

            bool sprinting = sprintPhase.Value == SprintPhase.Sprinting;
            // Vanilla buff sheet indices: Speed = 9, Exhausted = 25.
            int sheetIndex = sprinting ? 9 : 25;
            string label = sprinting ? "Horse Sprint" : "Horse Exhausted";
            int secondsLeft = (int)System.Math.Ceiling(sprintTimer.Value / 1000f);
            string timeText = (secondsLeft / 60) + ":" + (secondsLeft % 60).ToString("00");

            SpriteBatch b = e.SpriteBatch;
            const int iconSize = 64;
            int x = Game1.uiViewport.Width - iconSize - 24;
            int y = 24;

            if (sprinting && sprintBuffIcon != null)
            {
                b.Draw(sprintBuffIcon, new Rectangle(x, y, iconSize, iconSize), Color.White);
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

            if (e.Button == SButton.Z)
            {
                this.Monitor.Log(
                    $"Player tile: {Game1.player.Tile} | mounted: {Game1.player.isRidingHorse()} | location: {Game1.currentLocation?.Name}",
                    LogLevel.Info);
                return;
            }

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
            NPC? lewis = festival?.getActorByName("Lewis");
            if (lewis == null)
                return;

            if (Vector2.Distance(Game1.player.Tile, lewis.Tile) > 2f)
                return;

            this.Helper.Input.Suppress(e.Button);

            Response[] yesNo =
            {
                new("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                new("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
            };

            if (competitor.Value == null)
            {
                if (IsHost)
                {
                    // Host without horse: borrow + start race in one step.
                    Game1.currentLocation.createQuestionDialogue(
                        "Lewis: It looks like you don't have a horse! Marnie has some available to borrow. Ready to ride one and start the race?",
                        yesNo,
                        (_, answer) =>
                        {
                            if (answer != "Yes") return;
                            this.AssignBorrowedHorse();
                            Game1.drawObjectDialogue($"Lewis: Great! {competitor.Value!.Name} here will treat you well!");
                            Game1.afterDialogues = this.BeginRace;
                        }, lewis);
                }
                else
                {
                    // Non-host without horse: borrow only (host starts the race separately).
                    Game1.currentLocation.createQuestionDialogue(
                        "Lewis: It looks like you don't have a horse! Marnie has some available to borrow for the race. Would you like to ride one?",
                        yesNo,
                        (_, answer) =>
                        {
                            if (answer != "Yes") return;
                            this.AssignBorrowedHorse();
                            Game1.drawObjectDialogue($"Lewis: Great! {competitor.Value!.Name} here will treat you well!");
                        }, lewis);
                }
                return;
            }

            // Only the host can start the race; other players wait for the ready check.
            if (!IsHost)
            {
                Game1.drawObjectDialogue("Lewis: We're just waiting on the host to start the race!");
                return;
            }

            Game1.currentLocation.createQuestionDialogue("Ready to start the race?", yesNo,
                (_, answer) =>
                {
                    if (answer == "Yes")
                        this.BeginRace();
                }, lewis);
        }

        private static bool IsHost =>
            !Game1.IsMultiplayer || Game1.serverHost == null || Game1.player.Equals(Game1.serverHost.Value);

        private void BeginRace()
        {
            if (!Game1.IsMultiplayer)
            {
                this.LineUp();
                return;
            }

            this.OpenRaceReadyCheck();
            this.Helper.Multiplayer.SendMessage(true, MsgOpenReadyCheck,
                modIDs: new[] { this.Helper.ModRegistry.ModID });
        }

        /// <summary>ReadyCheckDialog manages Game1.netReady and fires onConfirm on each client once all are ready.</summary>
        private void OpenRaceReadyCheck()
        {
            if (readyCheckOpen.Value || phase.Value != Phase.Pasture)
                return;

            readyCheckOpen.Value = true;
            Game1.netReady.SetLocalReady(ReadyCheckName, ready: true);
            Game1.activeClickableMenu = new ReadyCheckDialog(
                ReadyCheckName,
                allowCancel: true,
                onConfirm: (_) =>
                {
                    Game1.exitActiveMenu();
                    readyCheckOpen.Value = false;
                    this.LineUp();
                },
                onCancel: (_) =>
                {
                    Game1.netReady.SetLocalReady(ReadyCheckName, ready: false);
                    readyCheckOpen.Value = false;
                });
        }

        private void LineUp()
        {
            this.EnsureCompetitorHorse();
            Horse? horse = competitor.Value;
            GameLocation loc = Game1.currentLocation;
            if (horse == null || loc == null)
                return;

            phase.Value = Phase.Racing;

            this.SetLayerVisible("Racing", true);
            this.DespawnSpectators();
            this.SpawnSpectators(racingSpectators);

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

            // Mount directly without the NetMutex — the async round-trip can silently fail during festival
            // events. Setting rider + mounting.Value = true is enough; Horse.update finalizes the mount state.
            horse.rider = Game1.player;
            horse.mounting.Value = true;
            Game1.player.synchronizedJump(6f);
            Game1.player.freezePause = 5000;
            Game1.player.Halt();
            Game1.player.UsingTool = false;

            this.SpawnNpcRacers(loc, System.Math.Max(1, Game1.getOnlineFarmers().Count()));

            Game1.playSound(RaceStartSoundCue);
            this.startCountdown.Value = RaceStartSoundMs;
        }

        // Slot 0 = center, odd slots go below (+), even slots above (-), alternating outward.
        private static int StallYOffset(int slot) =>
            slot == 0 ? 0 : slot % 2 == 1 ? ((slot + 1) / 2) * 2 : -(slot / 2) * 2;

        private static Point StallHorseTile(int slot) => new(StartStall.X, StartStall.Y + StallYOffset(slot));

        /// <summary>Build a fenced stall per online player with a gate on the east wall.
        /// The stall fully encloses the rider; they're held by CanMove and input is suppressed until release
        /// so Fence.checkForAction's "free the trapped farmer" logic never fires.</summary>
        private void SpawnStartingStalls()
        {
            GameLocation loc = Game1.currentLocation;
            if (stallsSpawned.Value || loc == null)
                return;

            int count = System.Math.Max(1, Game1.getOnlineFarmers().Count()) + NpcRiderNames.Length;

            int minYOffset = 0, maxYOffset = 0;
            for (int i = 0; i < count; i++)
            {
                int yo = StallYOffset(i);
                if (yo < minYOffset) minYOffset = yo;
                if (yo > maxYOffset) maxYOffset = yo;
            }
            int topY = StartStall.Y + minYOffset - 1;
            int bottomY = StartStall.Y + maxYOffset + 1;

            for (int y = topY; y <= bottomY; y++)
                this.AddStallFence(loc, new Vector2(StartStall.X - 1, y), isGate: false);

            // The gate at (X+1, hy) must be flanked above and below by fence so its drawSum == 1500
            // (a vertical line). Without those flanks, Fence.updateWhenCurrentLocation auto-closes it every tick.
            for (int i = 0; i < count; i++)
            {
                int hy = StallHorseTile(i).Y;
                this.AddStallFence(loc, new Vector2(StartStall.X, hy - 1), isGate: false);
                this.AddStallFence(loc, new Vector2(StartStall.X, hy + 1), isGate: false);
                this.AddStallFence(loc, new Vector2(StartStall.X + 1, hy - 1), isGate: false);
                this.AddStallFence(loc, new Vector2(StartStall.X + 1, hy + 1), isGate: false);
                this.AddStallFence(loc, new Vector2(StartStall.X + 1, hy), isGate: true);
            }

            stallsSpawned.Value = true;
        }

        /// <summary>Place a fence or gate, skipping already-occupied tiles. Gates use <see cref="RaceStartGate"/>
        /// so an open gate has no collision footprint inside the festival.</summary>
        private void AddStallFence(GameLocation loc, Vector2 tile, bool isGate)
        {
            if (loc.objects.ContainsKey(tile))
                return;
            Fence fence = isGate ? new RaceStartGate(tile) : new Fence(tile, "322", isGate: false);
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
                if (this.startCountdown.Value > 0f)
                    return;
            }

            this.startCountdown.Value = -1f;
            Game1.player.CanMove = true;
            raceStartTime.Value = Game1.currentGameTime.TotalGameTime;
            int totalPlayers = System.Math.Max(1, Game1.getOnlineFarmers().Count());
            int totalSlots = totalPlayers + NpcRiderNames.Length;
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
            Vector2 gateTile = new(StartStall.X + 1, StallHorseTile(slot).Y);
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
            bool inBand = t.X >= FinishMin.X && t.X <= FinishMax.X
                       && t.Y >= FinishMin.Y && t.Y <= FinishMax.Y;
            if (!inBand)
                return;

            phase.Value = Phase.Finished;
            Game1.addHUDMessage(new HUDMessage("Finished!", HUDMessage.achievement_type));
            var elapsed = Game1.currentGameTime.TotalGameTime - raceStartTime.Value;
            this.LogVerbose($"Race time for {Game1.player.Name}: {(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}");

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
            if (!(t.Y < DqZoneNorthOfY && t.X > DqZoneEastOfX))
                return;

            disqualified.Value = true;
            phase.Value = Phase.Finished;
            Game1.player.CanMove = false;

            // Teleport player and horse to just past the finish line so they wait in the spectator area.
            var arrivalOffset = new Vector2(0f, 64f);
            Game1.player.Position = TileToPixels(DqArrivalTile) + arrivalOffset;
            Horse? dqHorse = competitor.Value;
            if (dqHorse != null)
            {
                dqHorse.Position = TileToPixels(DqArrivalTile) + arrivalOffset;
                dqHorse.Halt();
            }

            NPC? lewis = RaceFestival?.getActorByName("Lewis");
            lewis?.doEmote(12);
            if (lewis != null)
            {
                lewis.CurrentDialogue.Clear();
                lewis.CurrentDialogue.Push(new Dialogue(lewis, "HorseTycoon.DQ",
                    "$a You've gone off the track! I'm afraid you are disqualified from this race."));
                Game1.drawDialogue(lewis);
            }
            else
                Game1.drawObjectDialogue(
                    "Lewis: You've gone off the track! I'm afraid you are disqualified from this race.");


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
            this.LogVerbose($"Farmer {farmerId} disqualified — recording as last-place finish.");
            this.RecordFinish(farmerId);
        }

        private void RecordFinish(long farmerId)
        {
            if (FinishOrder.Contains(farmerId))
                return;
            FinishOrder.Add(farmerId);
            this.LogVerbose($"Finish order recorded: position {FinishOrder.Count} = farmer {farmerId}");

            int totalPlayers = Game1.getOnlineFarmers().Count() + NpcRiderNames.Length;
            if (FinishOrder.Count >= totalPlayers && HostCeremonyCountdown < 0f)
                HostCeremonyCountdown = CeremonyDelayMs;
        }

        private void BroadcastCeremony()
        {
            // DQ'd players always place last; sort them behind everyone who finished legitimately.
            var orderedIds = FinishOrder
                .OrderBy(id => DisqualifiedFarmers.Contains(id) ? 1 : 0)
                .ToList();
            this.Helper.Multiplayer.SendMessage(
                new StartCeremonyMessage(orderedIds),
                MsgStartCeremony,
                modIDs: new[] { this.Helper.ModRegistry.ModID });
            // Host handles it locally — SendMessage does not deliver to the sender.
            this.StartCeremony(orderedIds);
        }

        private void StartCeremony(List<long> rankedPlayerIds)
        {
            phase.Value = Phase.Ceremony;
            ceremonyOrder.Value = new List<long>(rankedPlayerIds);
            ceremonyStep.Value = 0;

            long localId = Game1.player.UniqueMultiplayerID;
            int placement = rankedPlayerIds.IndexOf(localId); // 0-based; -1 if not in top 3+

            var ceremonyOffset = new Microsoft.Xna.Framework.Vector2(0f, 64f);

            // Move the local rider + horse to their podium or spectator tile.
            if (placement >= 0 && placement < WinnersCircleTiles.Length)
            {
                Point podium = WinnersCircleTiles[placement];
                Game1.player.Position = TileToPixels(podium) + ceremonyOffset;
                Game1.player.faceDirection(Game1.up);

                Horse? horse = competitor.Value;
                if (horse != null)
                {
                    horse.Position = TileToPixels(podium) + ceremonyOffset;
                    horse.faceDirection(Game1.up);
                }
            }
            else
            {
                int spectatorSlot = System.Math.Max(0, placement - WinnersCircleTiles.Length);
                Point spec = SpectatorTiles[System.Math.Min(spectatorSlot, SpectatorTiles.Length - 1)];
                Game1.player.Position = TileToPixels(spec) + ceremonyOffset;
                Game1.player.faceDirection(Game1.up);

                Horse? horse = competitor.Value;
                if (horse != null)
                {
                    horse.Position = TileToPixels(spec) + ceremonyOffset;
                    horse.faceDirection(Game1.up);
                }
            }

            // Move NPC racers to their podium or spectator tiles.
            int npcSpectatorSlot = 0;
            for (int i = 0; i < rankedPlayerIds.Count; i++)
            {
                long id = rankedPlayerIds[i];
                if (id >= 0) continue; // human player — handled above
                NpcRacer? racer = npcRacers.Find(r => r.FakeId == id);
                if (racer == null) continue;
                racer.Horse.controller = null;
                racer.Horse.Halt();
                if (i < WinnersCircleTiles.Length)
                {
                    Point podium = WinnersCircleTiles[i];
                    racer.Horse.Position = TileToPixels(podium) + ceremonyOffset;
                    racer.Horse.faceDirection(Game1.up);
                }
                else
                {
                    Point spec = SpectatorTiles[System.Math.Min(npcSpectatorSlot, SpectatorTiles.Length - 1)];
                    npcSpectatorSlot++;
                    racer.Horse.Position = TileToPixels(spec) + ceremonyOffset;
                    racer.Horse.faceDirection(Game1.up);
                }
                if (racer.Rider != null)
                    SyncRiderToHorse(racer.Rider, racer.Horse);
            }

            Game1.player.CanMove = false;
            Game1.player.Halt();

            // Freeze camera on the center of the winner's circle.
            Point center = WinnersCircleTiles[System.Math.Min(1, WinnersCircleTiles.Length - 1)];
            Game1.viewportFreeze = true;
            Game1.viewport.X = System.Math.Max(0, center.X * 64 - Game1.viewport.Width / 2);
            Game1.viewport.Y = System.Math.Max(0, center.Y * 64 - Game1.viewport.Height / 2);

            // Move Lewis to the announcer tile.
            NPC? lewis = RaceFestival?.getActorByName("Lewis");
            if (lewis != null)
            {
                lewis.Position = TileToPixels(LewisAnnouncerTile);
                lewis.faceDirection(Game1.down);
            }

            this.SetLayerVisible("Racing", false);
            this.SetLayerVisible("AwardsEvent", true);
            this.DespawnSpectators();
            this.SpawnSpectators(ceremonySpectators);

            this.LogVerbose($"Ceremony started. Local placement (0-based): {placement}");
        }

        private bool prizeMenuWasOpen = false;

        private void UpdateCeremony()
        {
            bool prizeMenuOpen = Game1.activeClickableMenu is ItemGrabMenu;

            // The event system triggers a screen fade when the ItemGrabMenu closes during a festival.
            // Detect that transition and immediately clear the fade before it renders.
            if (prizeMenuWasOpen && !prizeMenuOpen)
            {
                Game1.fadeToBlack = false;
                Game1.fadeToBlackAlpha = 0f;
            }
            prizeMenuWasOpen = prizeMenuOpen;

            // Wait for any open dialogue/menu to be dismissed before advancing.
            if (Game1.activeClickableMenu != null)
                return;
            this.AdvanceCeremonyStep();
        }

        private void AdvanceCeremonyStep()
        {
            ceremonyStep.Value++;

            List<long> order = ceremonyOrder.Value;
            long localId = Game1.player.UniqueMultiplayerID;

            switch (ceremonyStep.Value)
            {
                case 1: // Opening line
                    Game1.drawObjectDialogue(
                        "Lewis: What a spectacular race! Let's see how our riders placed!");
                    break;

                case 2: // Announce 3rd place
                    if (order.Count >= 3)
                        Game1.drawObjectDialogue(
                            $"Lewis: In 3rd place... {this.GetRacerName(order[2])}! Congratulations!");
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 3: // Prize for 3rd
                    if (order.Count >= 3 && order[2] == localId)
                        AwardPrizes("(O)PrizeTicket");
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 4: // Announce 2nd place
                    if (order.Count >= 2)
                        Game1.drawObjectDialogue(
                            $"Lewis: In 2nd place... {this.GetRacerName(order[1])}! Well done!");
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 5: // Prize for 2nd
                    if (order.Count >= 2 && order[1] == localId)
                        AwardPrizes("(O)PrizeTicket");
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 6: // Announce 1st place
                    if (order.Count >= 1)
                    {
                        Game1.drawObjectDialogue(
                            $"Lewis: And the winner is... {this.GetRacerName(order[0])}! " +
                            "What a ride! You've earned a diamond and a prize ticket!");
                        Game1.playSound("achievement");
                    }
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 7: // Prize for 1st
                    if (order.Count >= 1 && order[0] == localId)
                        AwardPrizes("(O)PrizeTicket", "(O)72", "(F)CP.HorseTycoon.HorseStatue");
                    else
                        this.AdvanceCeremonyStep();
                    break;

                case 8: // Closing line
                    Game1.drawObjectDialogue(
                        "Lewis: Thank you all for participating in the Spring Horse Festival! " +
                        "See you next year!");
                    break;

                case 9: // End festival
                    this.EndFestival();
                    break;
            }
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

        private static void AwardPrizes(params string[] itemIds)
        {
            var items = itemIds.Select(id => ItemRegistry.Create(id)).ToList<Item>();
            Game1.activeClickableMenu = new ItemGrabMenu(
                items, false, true, InventoryMenu.highlightAllItems, null, null);
        }

        private void EndFestival()
        {
            Game1.viewportFreeze = false;
            Game1.player.CanMove = true;

            // Do NOT despawn NPCs here — the temp map is still active during the fade and
            // they must stay visible until the screen is fully black. Reset() clears the
            // lists once the player has warped back to the real Forest.
            Event? festival = RaceFestival;
            if (festival != null)
                festival.endBehaviors(new[] { "end" }, Game1.currentLocation);
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

            for (int i = 0; i < NpcRiderNames.Length; i++)
            {
                string name = NpcRiderNames[i];
                Point holdTile = i < NpcRiderHoldingTiles.Length
                    ? NpcRiderHoldingTiles[i]
                    : new Point(92, 20 + i * 2);
                var actor = new NPC(
                    new AnimatedSprite("Characters/" + name, 0, 16, 32),
                    TileToPixels(holdTile),
                    Game1.left,
                    name);
                actor.EventActor = true;
                festLoc.characters.Add(actor);
                spawnedRiders.Add(actor);
                this.LogVerbose($"Spawned rider actor '{name}' at holding tile {holdTile}.");
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
        // already handles them — processing them here would create duplicate event actors).
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
                            this.LogVerbose($"ReadNpcPlacements('{layerName}'): skipping '{name}' — not in characterData (SVE not installed?).");
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
                            this.LogVerbose($"ReadNpcPlacements('{layerName}'): skipping '{name}' — not in characterData (East Scarp not installed?).");
                        // TODO: re-enable met check before shipping
                        //else if (!Game1.getAllFarmers().Any(f => f.friendshipData.ContainsKey(name)))
                        //    this.LogVerbose($"ReadNpcPlacements('{layerName}'): skipping '{name}' — not met by any attending farmer.");
                        else
                            results.Add(new NpcSpectatorPlacement(name, new Point(x, y), dir));
                    }
                }

            if (placeholderSlots.Count > 0)
            {
                var modNpcs = this.GetMetModNpcNames();
                for (int i = 0; i < placeholderSlots.Count; i++)
                {
                    if (i >= modNpcs.Count) break;
                    var (tile, dir) = placeholderSlots[i];
                    results.Add(new NpcSpectatorPlacement(modNpcs[i], tile, dir, IsAutoFilled: true));
                    this.LogVerbose($"ReadNpcPlacements('{layerName}'): assigned mod NPC '{modNpcs[i]}' to placeholder at {tile}.");
                }
                if (modNpcs.Count < placeholderSlots.Count)
                    this.LogVerbose($"ReadNpcPlacements('{layerName}'): {placeholderSlots.Count - modNpcs.Count} placeholder slot(s) left unfilled (not enough met mod NPCs).");
            }

            this.LogVerbose($"ReadNpcPlacements('{layerName}'): {results.Count} entries ({placeholderSlots.Count} placeholder slot(s)).");
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

            this.LogVerbose($"GetMetModNpcNames: found {met.Count} met mod NPC(s): {string.Join(", ", met)}");
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
                // Create a fresh event-actor NPC — same approach as the game's addTemporaryActor command.
                // This leaves the real NPC untouched at home with their normal animation state.
                var sprite = new AnimatedSprite("Characters/" + p.Name, 0, 16, 32);
                var actor = new NPC(sprite, TileToPixels(p.Tile), p.Direction, p.Name);
                actor.faceDirection(p.Direction);
                actor.Sprite.StopAnimation();
                actor.EventActor = true;
                if (p.IsAutoFilled)
                {
                    // Auto-filled NPCs have no festival-specific dialog, so give them a generic line.
                    // Pick deterministically from the NPC's name so all clients agree.
                    int idx = System.Math.Abs(p.Name.GetHashCode()) % AutoFilledSpectatorDialogKeys.Length;
                    string text = this.Helper.Translation.Get(AutoFilledSpectatorDialogKeys[idx]);
                    actor.TemporaryDialogue = new System.Collections.Generic.Stack<Dialogue>();
                    actor.TemporaryDialogue.Push(new Dialogue(actor, "HorseTycoon.spectator." + p.Name, text));
                }
                festLoc.characters.Add(actor);
                spawnedSpectators.Add(actor);
            }
        }

        private void DespawnSpectators()
        {
            var festLoc = Game1.currentLocation;
            foreach (NPC actor in spawnedSpectators)
                festLoc?.characters.Remove(actor);
            spawnedSpectators.Clear();
        }


        /// <summary>
        /// Spawn one Horse + rider pair per NpcRiderNames entry into the stalls immediately after
        /// the player slots. Idempotent — guarded by <see cref="npcRacersSpawned"/>.
        /// </summary>
        private void SpawnNpcRacers(GameLocation loc, int playerSlotCount)
        {
            if (npcRacersSpawned) return;
            npcRacersSpawned = true;

            this.SpawnNpcRiders();

            var rng = new System.Random(
                (int)(Game1.uniqueIDForThisGame ^ (uint)Game1.Date.TotalDays));

            int npcSlots = System.Math.Max(0, MaxRacers - playerSlotCount);
            if (npcSlots < NpcRiderNames.Length)
                this.Monitor.Log(
                    $"Race is full ({playerSlotCount} players, max {MaxRacers}) — dropping {NpcRiderNames.Length - npcSlots} NPC racer(s).",
                    LogLevel.Info);

            for (int i = 0; i < System.Math.Min(NpcRiderNames.Length, npcSlots); i++)
            {
                string riderName = NpcRiderNames[i];
                int slot = playerSlotCount + i;
                Point stallTile = StallHorseTile(slot);

                // The rider NPC must already be in the festival location (placed by the event script).
                NPC? rider = loc.characters.OfType<NPC>()
                    .FirstOrDefault(c => c.Name == riderName);
                if (rider == null)
                {
                    this.Monitor.Log(
                        $"NPC racer '{riderName}' not found in festival location — skipping.",
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
                SetIdleAnimation(horse);

                // Seat the rider on the horse. drawOnTop ensures the rider renders above the horse sprite.
                rider.EventActor = true;
                rider.drawOnTop = true;
                SyncRiderToHorse(rider, horse);

                int yearBonus = Game1.year >= 2 ? 5 : 0;
                int speedIV = NpcRiderSpeeds[i % NpcRiderSpeeds.Length] + yearBonus;
                int sprintIV = NpcRiderSprints[i % NpcRiderSprints.Length] + yearBonus;

                var racer = new NpcRacer
                {
                    Horse = horse,
                    Rider = rider,
                    FakeId = nextNpcFakeId--,
                    TotalSpeed = speedIV,
                    TotalSprint = sprintIV,
                    Route = NpcRaceRoutes[i == 0 ? 2 : i % NpcRaceRoutes.Length],
                    WaypointIndex = 0,
                    NextSprintCheckMs = (float)(rng.NextDouble() * 5000.0 + 3000.0),
                };
                npcRacers.Add(racer);

                this.LogVerbose($"NPC racer '{riderName}' in slot {slot} — Speed={speedIV}, Sprint={sprintIV}");
            }

            // The two fastest NPCs track the race leader; the rest track the nearest farmer.
            foreach (NpcRacer r in npcRacers.OrderByDescending(r => r.TotalSpeed).Take(2))
                r.MatchLeader = true;
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

                // Stop only when the full route is exhausted — not on finish crossing, so the
                // horse rides through the finish band to its final waypoint past the line.
                if (r.PathIndex >= r.ComputedPath.Count && r.WaypointIndex >= r.Route.Length)
                {
                    if (!r.MovementDone)
                    {
                        r.MovementDone = true;
                        SetGrazingAnimation(r.Horse);
                    }
                    continue;
                }

                this.UpdateNpcSprint(r, deltaMs, npcIdx);

                // Compute A* path to next waypoint when the previous segment is exhausted.
                if (r.PathIndex >= r.ComputedPath.Count && r.WaypointIndex < r.Route.Length)
                    TryComputePathToWaypoint(r, loc, r.Route[r.WaypointIndex++]);

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
                    this.LogVerbose($"[Match AI] {r.Rider?.Name ?? r.Horse.Name}: all players finished — resuming normal speed");
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
                            this.LogVerbose(
                                $"[Match AI] {r.Rider?.Name ?? r.Horse.Name}: {direction} to {roundedMultiplier:F2}x " +
                                $"(NPC waypoint {r.WaypointIndex}, player nearest waypoint {playerWpIdx}/{r.Route.Length - 1}, " +
                                $"tracking {trackMode} {targetFarmer.Name} {targetTileDist:F1} tiles away)");
                            r.LastMatchMultiplier = roundedMultiplier;
                        }
                    }
                    else if (r.LastMatchMultiplier != 1f)
                    {
                        this.LogVerbose($"[Match AI] {r.Rider?.Name ?? r.Horse.Name}: back to normal speed (target player {targetTileDist:F1} tiles away)");
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
                        loc.localSound("thudStep");
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

                // Finish detection — record the crossing but keep moving; the route ends past the line.
                if (!r.Finished)
                {
                    Vector2 t = r.Horse.Tile;
                    if (t.X >= FinishMin.X && t.X <= FinishMax.X
                        && t.Y >= FinishMin.Y && t.Y <= FinishMax.Y)
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
        /// <paramref name="r"/>.ComputedPath. The controller itself is discarded — we drive
        /// position directly so we never call MovePosition (blocked during eventUp).
        /// </summary>
        private bool TryComputePathToWaypoint(NpcRacer r, GameLocation loc, Point dest)
        {
            if (PfcCtor == null || PfcPathField == null)
            {
                this.Monitor.Log("PathFindController reflection unavailable — NPC horses cannot pathfind.", LogLevel.Error);
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
            if (r.NpcSprintPhase == SprintPhase.Sprinting)
                tilesPerSec *= 1f + (r.TotalSprint * 0.005f);
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
                float durationMs = System.Math.Clamp((r.TotalSprint / 4f) * 1000f, 1000f, 25000f);
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

        // ======================== End NPC Racer System ========================

        private void RestartRace()
        {
            if (RaceFestival == null)
            {
                this.Monitor.Log("ht_race_restart: not currently in the horse festival.", LogLevel.Warn);
                return;
            }

            // Capture competitor before Reset() clears it.
            Horse? horse = competitor.Value ?? Game1.player.mount;

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
            if (horse != null)
            {
                int slot = PastureSlotFor(Game1.player);
                PlaceHorseInPasture(horse, slot);
                Game1.player.Position = TileToPixels(new Point(56, 10));
                lastRiddenMount.Value = horse;
                lastMountedTick.Value = Game1.ticks;
            }

            this.Monitor.Log("ht_race_restart: reset to pasture phase.", LogLevel.Info);
        }

        private void Reset()
        {
            this.RestoreSuppressedBuffs();
            this.RemoveStartingStalls();
            this.startCountdown.Value = -1f;
            sprintPhase.Value = SprintPhase.Ready;
            sprintTimer.Value = 0f;
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
            phase.Value = Phase.None;
            competitor.Value = null;
            this.SetLayerVisible("Racing", false);
            this.SetLayerVisible("AwardsEvent", false);
            this.DespawnSpectators();
            setupSpectators = null;
            racingSpectators = null;
            ceremonySpectators = null;
            wanderMoving.Value = false;
            wanderDir.Value = -1;
            wanderTicks.Value = 0;
            readyCheckOpen.Value = false;
            ceremonyOrder.Value.Clear();
            ceremonyStep.Value = 0;
            disqualified.Value = false;
            // Static host-side state — safe to clear on any screen since only the host writes to these.
            FinishOrder.Clear();
            DisqualifiedFarmers.Clear();
            HostCeremonyCountdown = -1f;
            Game1.viewportFreeze = false;

            // NPC racer cleanup: remove AI horses; restore rider NPCs to their original locations.
            foreach (NpcRacer r in npcRacers)
            {
                r.Horse.controller = null;
                r.Horse.currentLocation?.characters.Remove(r.Horse);
            }
            npcRacers.Clear();
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

        public RaceStartGate(Vector2 tile) : base(tile, "325", isGate: true) { }

        public override Rectangle GetBoundingBoxAt(int x, int y) =>
            this.gatePosition.Value >= 88 ? Rectangle.Empty : base.GetBoundingBoxAt(x, y);

        // PathFindController A* uses isTileOccupied → obj.isPassable() to decide walkability.
        // Without this override the open gate still blocks NPC horse pathfinding.
        public override bool isPassable() => this.gatePosition.Value >= 88;
    }
}
