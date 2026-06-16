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
                    this.CheckFinish();
                    break;
                case Phase.Finished:
                    AdvanceHorseAnimations();
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

            int count = System.Math.Max(1, Game1.getOnlineFarmers().Count());

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
            for (int i = 0; i < totalPlayers; i++)
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

            int totalPlayers = Game1.getOnlineFarmers().Count();
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
                            $"Lewis: In 3rd place... {GetFarmerName(order[2])}! Congratulations!");
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
                            $"Lewis: In 2nd place... {GetFarmerName(order[1])}! Well done!");
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
                            $"Lewis: And the winner is... {GetFarmerName(order[0])}! " +
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

            Event? festival = RaceFestival;
            if (festival != null)
                festival.endBehaviors(new[] { "end" }, Game1.currentLocation);
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
    }
}
