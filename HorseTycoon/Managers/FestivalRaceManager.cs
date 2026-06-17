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
        private static readonly Point StartStall = new(39, 47);
        // Finish band (inclusive tile rectangle).
        private static readonly Point FinishMin = new(38, 11);
        private static readonly Point FinishMax = new(40, 17);
        // Decorative pony-ride horse in the pen to the left of Leah's house.
        private static readonly Point PenHorseTile = new(94, 31);
        private static readonly string[] AllSkins = { "Roan", "BlueRoan", "Dapple", "Bay", "Belgian", "Shire", "Chestnut" };

        private const int WanderRepickMinTicks = 60;
        private const int WanderRepickMaxTicks = 180;

        // NPC racers: Marnie, Leah, Abigail ride in the race as AI opponents.
        private static readonly string[] NpcRiderNames = { "Marnie", "Leah", "Abigail" };

        // Course waypoints: east → south over bridge → west over bridges → north to finish.
        private static readonly Point[] NpcRaceWaypoints =
        {
            new(85, 50),  // east corridor
            new(89, 69),  // south, approaching bridge
            new(57, 70),  // west, after first bridge
            new(39, 84),  // continuing west/south
            new(39, 97),  // south bridge area
            new(16, 80),  // west, after bridges
            new(20, 30),  // north heading
            new(39, 14),  // into the finish band
        };

        // Pixel offset applied to each rider NPC so they appear seated on the horse.
        // Negative Y moves the sprite visually upward on screen; tune if the rider looks off.
        private static readonly Vector2 RiderOffset = new(0f, -24f);

        // The game dismounts the player as they warp into the festival, so we capture the mount each tick
        // beforehand and treat them as "arrived mounted" if they were riding within this window.
        private const int EntryMountWindowTicks = 600;

        // Winner's circle tiles (1st, 2nd, 3rd place left-to-right), and Lewis's announcer position.
        // Tune in-game with `ht_race_tile`.
        private static readonly Point[] WinnersCircleTiles =
        {
            new(36, 13), // 1st place
            new(34, 13), // 2nd place
            new(32, 13), // 3rd place
        };
        private static readonly Point LewisAnnouncerTile = new(34, 10);

        // Delay between the last player crossing the finish line and the ceremony starting.
        private const float CeremonyDelayMs = 2000f;

        private enum Phase { None, Pasture, Racing, Finished, Ceremony }

        private class NpcRacer
        {
            public Horse Horse = null!;
            public NPC? Rider;
            public long FakeId;
            public int TotalSpeed;
            public int TotalSprint;
            public int WaypointIndex;
            public bool Finished;
            public SprintPhase NpcSprintPhase = SprintPhase.Ready;
            public float NpcSprintTimer;
            public float NextSprintCheckMs;
            public int LastAnimDir = -1;
            // A*-computed tile path to the current waypoint; driven by direct position updates.
            public List<Point> ComputedPath = new();
            public int PathIndex;
        }

        private readonly IModHelper Helper;
        private readonly IMonitor Monitor;

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
        // -1 means inactive. Kept in real time so it ticks while the festival pauses the game clock.
        private readonly PerScreen<float> startCountdown = new(() => -1f);
        private readonly PerScreen<List<Buff>> suppressedBuffs = new(() => new List<Buff>());
        private readonly PerScreen<SprintPhase> sprintPhase = new(() => SprintPhase.Ready);
        private readonly PerScreen<float> sprintTimer = new(() => 0f);

        private readonly PerScreen<List<long>> ceremonyOrder = new(() => new List<long>());
        private readonly PerScreen<int> ceremonyStep = new(() => 0);

        // NPC racers are the same object list for every screen; spawned once per race via guard flags.
        private readonly List<NpcRacer> npcRacers = new();
        // Tracks NPCs borrowed from their world locations so they can be restored on festival end.
        private readonly List<(NPC Npc, GameLocation OriginalLocation)> borrowedRiders = new();
        private bool npcRidersBorrowed = false;
        private bool npcRacersSpawned = false;
        private long nextNpcFakeId = -1L;

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
        private const string MsgOpenReadyCheck = "OpenReadyCheck";
        private const string MsgPlayerFinished = "PlayerFinished";
        private const string MsgStartCeremony = "StartCeremony";
        private record StartCeremonyMessage(List<long> RankedPlayerIds);

        // Custom start sound registered as a Data/AudioChanges cue in [CP] HorseTycoon/data/sound.json.
        private const string RaceStartSoundCue = "CP.HorseTycoon_RaceStart";
        private const int RaceStartSoundMs = 8000;

        public void Initialize()
        {
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
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.getMovementSpeed)),
                transpiler: new HarmonyMethod(typeof(FestivalRaceManager), nameof(GetMovementSpeed_Transpiler)));

            // Block dismounting during the race. Patching checkAction (which initiates the dismount slide)
            // rather than dismount() prevents the rider getting stuck mid-slide.
            harmony.Patch(
                original: AccessTools.Method(typeof(Horse), nameof(Horse.checkAction)),
                prefix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(CheckAction_Prefix)));

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
                    this.UpdateNpcRacers();
                    this.CheckFinish();
                    break;
                case Phase.Finished:
                    AdvanceHorseAnimations();
                    this.UpdateNpcRacers();
                    break;
                case Phase.Ceremony:
                    AdvanceHorseAnimations();
                    this.UpdateCeremony();
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

            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(Game1.player.mount);
            int totalSprint = horse?.GetHorseStats().TotalSprint ?? 0;
            float durationMs = System.Math.Clamp((totalSprint / 4f) * 1000f, 1000f, 100000f);

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

        private void EnterPasture(Event festival)
        {
            phase.Value = Phase.Pasture;
            competitor.Value = null;

            // showWorldCharacters must be set on every client so the event renderer draws horses placed by any client.
            festival.showWorldCharacters = true;

            // Every client builds its own stalls — the festival temp map's objects aren't net-synced.
            this.SpawnStartingStalls();
            this.SpawnPenHorse();
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

            this.Monitor.Log($"Brought '{horse.Name}' into the festival pasture (slot {slot}).", LogLevel.Debug);
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

            if (e.Type == MsgStartCeremony && RaceFestival != null)
            {
                this.StartCeremony(e.ReadAs<StartCeremonyMessage>().RankedPlayerIds);
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
            this.Monitor.Log($"Placed remote horse '{horse.Name}' in pasture slot {msg.Slot}.", LogLevel.Debug);
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
            Rectangle src = Game1.getSourceRectForStandardTileSheet(Game1.buffsIcons, sheetIndex, 16, 16);
            const int iconSize = 64;
            int x = Game1.uiViewport.Width - iconSize - 24;
            int y = 24;

            b.Draw(Game1.buffsIcons, new Vector2(x, y), src, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
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
                if (e.Button.IsActionButton() || e.Button.IsUseToolButton() || e.Button == SButton.R)
                    this.Helper.Input.Suppress(e.Button);
                return;
            }

            if (RaceRidingActive && e.Button == SButton.R)
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

            // Only the host can start the race; other players wait for the ready check.
            if (!IsHost)
            {
                Game1.drawObjectDialogue("Lewis: We're just waiting on the host to start the race.");
                return;
            }

            if (competitor.Value == null)
            {
                Game1.drawObjectDialogue("Lewis: Come back riding your horse if you want to race!");
                return;
            }

            Response[] answers =
            {
                new("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                new("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
            };
            Game1.currentLocation.createQuestionDialogue("Ready to start the race?", answers,
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
            Horse? horse = competitor.Value;
            GameLocation loc = Game1.currentLocation;
            if (horse == null || loc == null)
                return;

            phase.Value = Phase.Racing;

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
            Game1.drawObjectDialogue("You crossed the finish line!");

            if (IsHost)
                this.RecordFinish(Game1.player.UniqueMultiplayerID);
            else
                this.Helper.Multiplayer.SendMessage(
                    Game1.player.UniqueMultiplayerID,
                    MsgPlayerFinished,
                    modIDs: new[] { this.Helper.ModRegistry.ModID });
        }

        private void RecordFinish(long farmerId)
        {
            if (FinishOrder.Contains(farmerId))
                return;
            FinishOrder.Add(farmerId);
            this.Monitor.Log($"Finish order recorded: position {FinishOrder.Count} = farmer {farmerId}", LogLevel.Debug);

            int totalPlayers = Game1.getOnlineFarmers().Count() + NpcRiderNames.Length;
            if (FinishOrder.Count >= totalPlayers && HostCeremonyCountdown < 0f)
                HostCeremonyCountdown = CeremonyDelayMs;
        }

        private void BroadcastCeremony()
        {
            var orderedIds = new List<long>(FinishOrder);
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

            // Move the local rider + horse to their podium tile.
            if (placement >= 0 && placement < WinnersCircleTiles.Length)
            {
                Point podium = WinnersCircleTiles[placement];
                Game1.player.Position = TileToPixels(podium);
                Game1.player.faceDirection(Game1.up);

                Horse? horse = competitor.Value;
                if (horse != null)
                {
                    horse.Position = TileToPixels(podium);
                    horse.faceDirection(Game1.up);
                }
            }

            // Move NPC racers who placed in the top 3 to their podium tiles.
            for (int i = 0; i < System.Math.Min(rankedPlayerIds.Count, WinnersCircleTiles.Length); i++)
            {
                long id = rankedPlayerIds[i];
                if (id >= 0) continue;
                NpcRacer? racer = npcRacers.Find(r => r.FakeId == id);
                if (racer == null) continue;
                Point podium = WinnersCircleTiles[i];
                racer.Horse.controller = null;
                racer.Horse.Halt();
                racer.Horse.Position = TileToPixels(podium);
                racer.Horse.faceDirection(Game1.up);
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

            this.Monitor.Log($"Ceremony started. Local placement (0-based): {placement}", LogLevel.Debug);
        }

        private void UpdateCeremony()
        {
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
                        AwardPrizes("(O)PrizeTicket", "(O)72");
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

        private static string GetFarmerName(long uniqueId) =>
            Game1.getOnlineFarmers()
                .FirstOrDefault(f => f.UniqueMultiplayerID == uniqueId)
                ?.displayName ?? "Unknown";

        private string GetRacerName(long uniqueId)
        {
            if (uniqueId < 0)
            {
                NpcRacer? racer = npcRacers.Find(r => r.FakeId == uniqueId);
                return racer?.Rider?.displayName ?? racer?.Rider?.Name ?? "Mystery Rider";
            }
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
            this.RestoreNpcRiders();

            Game1.viewportFreeze = false;
            Game1.player.CanMove = true;

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
        private void BorrowNpcRiders()
        {
            if (npcRidersBorrowed) return;
            npcRidersBorrowed = true;

            GameLocation festLoc = Game1.currentLocation;

            for (int i = 0; i < NpcRiderNames.Length; i++)
            {
                string name = NpcRiderNames[i];
                NPC? npc = Game1.getCharacterFromName(name);
                if (npc == null)
                {
                    this.Monitor.Log($"Could not find NPC '{name}' to borrow for the race.", LogLevel.Warn);
                    continue;
                }

                GameLocation originalLoc = npc.currentLocation;
                borrowedRiders.Add((npc, originalLoc));

                originalLoc?.characters.Remove(npc);
                npc.currentLocation = festLoc;
                Point holdTile = i < NpcRiderHoldingTiles.Length
                    ? NpcRiderHoldingTiles[i]
                    : new Point(92, 20 + i * 2);
                npc.Position = TileToPixels(holdTile);
                npc.faceDirection(Game1.left);
                npc.EventActor = true;

                if (!festLoc.characters.Contains(npc))
                    festLoc.characters.Add(npc);

                this.Monitor.Log($"Borrowed '{name}' from {originalLoc?.Name ?? "null"} for the race.", LogLevel.Debug);
            }
        }

        /// <summary>
        /// Return all borrowed rider NPCs to their original world locations.
        /// Safe to call multiple times; clears <see cref="borrowedRiders"/> after running.
        /// </summary>
        private void RestoreNpcRiders()
        {
            foreach ((NPC npc, GameLocation originalLoc) in borrowedRiders)
            {
                npc.currentLocation?.characters.Remove(npc);
                npc.EventActor = false;
                npc.currentLocation = originalLoc;
                if (originalLoc != null && !originalLoc.characters.Contains(npc))
                    originalLoc.characters.Add(npc);
            }
            borrowedRiders.Clear();
            npcRidersBorrowed = false;
        }

        /// <summary>
        /// Spawn one Horse + rider pair per NpcRiderNames entry into the stalls immediately after
        /// the player slots. Idempotent — guarded by <see cref="npcRacersSpawned"/>.
        /// </summary>
        private void SpawnNpcRacers(GameLocation loc, int playerSlotCount)
        {
            if (npcRacersSpawned) return;
            npcRacersSpawned = true;

            this.BorrowNpcRiders();

            var rng = new System.Random(
                (int)(Game1.uniqueIDForThisGame ^ (uint)Game1.Date.TotalDays));

            for (int i = 0; i < NpcRiderNames.Length; i++)
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
                SetGrazingAnimation(horse);

                // Seat the rider on the horse.
                rider.EventActor = true;
                SyncRiderToHorse(rider, horse);

                // Randomize Special-quality stats (IV options: 20, 30, 40).
                int speedIV = rng.Next(2, 5) * 10;
                int sprintIV = rng.Next(2, 5) * 10;

                var racer = new NpcRacer
                {
                    Horse = horse,
                    Rider = rider,
                    FakeId = nextNpcFakeId--,
                    TotalSpeed = speedIV,
                    TotalSprint = sprintIV,
                    WaypointIndex = 0,
                    NextSprintCheckMs = (float)(rng.NextDouble() * 5000.0 + 3000.0),
                };
                npcRacers.Add(racer);

                this.Monitor.Log(
                    $"NPC racer '{riderName}' in slot {slot} — Speed={speedIV}, Sprint={sprintIV}",
                    LogLevel.Debug);
            }
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

            foreach (NpcRacer r in npcRacers)
            {
                if (r.Finished) continue;

                UpdateNpcSprint(r, deltaMs);

                // Compute A* path to next waypoint when the previous segment is exhausted.
                if (r.PathIndex >= r.ComputedPath.Count && r.WaypointIndex < NpcRaceWaypoints.Length)
                    TryComputePathToWaypoint(r, loc, NpcRaceWaypoints[r.WaypointIndex++]);

                // Drive position directly along the A*-computed tile path.
                // This bypasses MovePosition (blocked during eventUp) while still respecting
                // the collision-aware path that A* produced.
                float step = ComputeNpcSpeedPixelsPerMs(r) * deltaMs;
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
                        step -= dist;
                        r.PathIndex++;
                    }
                    else
                    {
                        diff.Normalize();
                        r.Horse.Position += diff * step;
                        r.Horse.faceDirection(GetFacingDirection(diff));
                        step = 0f;
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

                // Finish detection.
                Vector2 t = r.Horse.Tile;
                if (t.X >= FinishMin.X && t.X <= FinishMax.X
                    && t.Y >= FinishMin.Y && t.Y <= FinishMax.Y)
                {
                    r.Finished = true;
                    r.Horse.Halt();
                    if (IsHost)
                        this.RecordFinish(r.FakeId);
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

        private static float ComputeNpcSpeedPixelsPerMs(NpcRacer r)
        {
            float tilesPerSec = 2f + (r.TotalSpeed / 50f) * 1.5f;
            if (r.NpcSprintPhase == SprintPhase.Sprinting)
                tilesPerSec *= 1.5f;
            return tilesPerSec * 64f / 1000f;
        }

        private static int GetFacingDirection(Vector2 moveDir)
        {
            if (System.Math.Abs(moveDir.X) > System.Math.Abs(moveDir.Y))
                return moveDir.X > 0 ? Game1.right : Game1.left;
            return moveDir.Y > 0 ? Game1.down : Game1.up;
        }

        private static void UpdateNpcSprint(NpcRacer r, int deltaMs)
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

            // Ready — count down to the next sprint opportunity.
            r.NextSprintCheckMs -= deltaMs;
            if (r.NextSprintCheckMs > 0f) return;

            if (Game1.random.NextDouble() < 0.5)
            {
                float durationMs = System.Math.Clamp((r.TotalSprint / 4f) * 1000f, 1000f, 25000f);
                r.NpcSprintPhase = SprintPhase.Sprinting;
                r.NpcSprintTimer = durationMs;
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
            int dir = horse.FacingDirection;
            rider.FacingDirection = dir;
            // First frame of each directional row: down=0, right=4, up=8, left=12.
            rider.Sprite.currentFrame = dir * 4;
            rider.Sprite.StopAnimation();
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
                Game1.player.Position = TileToPixels(PastureSpawnForSlot(slot));
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
            phase.Value = Phase.None;
            competitor.Value = null;
            wanderMoving.Value = false;
            wanderDir.Value = -1;
            wanderTicks.Value = 0;
            readyCheckOpen.Value = false;
            ceremonyOrder.Value.Clear();
            ceremonyStep.Value = 0;
            // Static host-side state — safe to clear on any screen since only the host writes to these.
            FinishOrder.Clear();
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
            this.RestoreNpcRiders(); // safety net if EndFestival wasn't reached
            borrowedRiders.Clear();
            npcRidersBorrowed = false;
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
