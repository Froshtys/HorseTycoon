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

namespace HorseTycoon
{
    /// <summary>
    /// Drives the Spring 21 Horse Festival race in Cindersap Forest:
    /// the player rides their horse in, it wanders Marnie's pasture, talking to Lewis starts the race
    /// (teleport + auto-mount + countdown), then a finish line ends it.
    /// Multiplayer-aware: race start is gated by the vanilla <see cref="ReadyCheckDialog"/> sync primitive,
    /// and each client runs the warp/mount/countdown/finish for its own player. Local state is PerScreen.
    /// </summary>
    public class FestivalRaceManager
    {
        // Festival event id assigned by Event.tryToLoadFestival ("festival_" + <file key>).
        private const string FestivalEventId = "festival_spring21";
        private const string ReadyCheckName = "Froshty.HorseTycoon.horseRaceStart";

        // Custom sprint (the vanilla Buff timer is frozen during the festival, so the race runs its own).
        private const float SprintCooldownMs = 10000f; // exhaustion duration after a sprint
        private enum SprintPhase { Ready, Sprinting, Exhausted }

        // --- Tunable map coordinates (tiles) for CP.HorseTycoon_ForestFestival. Tune in-game with `ht_race_tile`. ---
        // Marnie's pasture: where the brought horse is placed and wanders.
        private static readonly Point PastureMin = new(80, 18);
        private static readonly Point PastureMax = new(90, 23);
        private static readonly Point PastureSpawn = new(98, 20);
        // Clockwise slot offsets (tiles) from PastureSpawn, one per player. Slot 0 = base, then left, up, right, down.
        private static readonly Point[] PastureSlotOffsets =
        {
            new(0, 0), new(-4, 0), new(0, -4), new(4, 0), new(0, 4),
        };
        // Lewis is placed via Set-Up_additionalCharacters in spring21.json; the race trigger reads his
        // live actor tile, so no constant is needed here.
        // Race start "right below the lake" (first lane); extra players are offset along X.
        private static readonly Point RaceStart = new(60, 47);
        // Finish band (inclusive tile rectangle). Crossing it ends the race.
        // Finish posts are at (39,11) and (39,17), so the line is X~39 between those Y values.
        private static readonly Point FinishMin = new(38, 11);
        private static readonly Point FinishMax = new(40, 17);

        // Cooldown between wander destinations (the horse walks there via vanilla pathfinding).
        private const int WanderRepickMinTicks = 60;        // ~1s
        private const int WanderRepickMaxTicks = 180;       // ~3s

        // The game dismounts the player as they warp into the festival, so we capture the mount each tick
        // beforehand and treat them as "arrived mounted" if they were riding within this many ticks of the
        // pasture phase beginning (covers the warp + set-up sequence; you can't remount during it).
        private const int EntryMountWindowTicks = 600; // ~10s

        private enum Phase { None, Pasture, Racing, Finished }

        private readonly IModHelper Helper;
        private readonly IMonitor Monitor;

        private readonly PerScreen<Phase> phase = new(() => Phase.None);
        // The real mountable Horse, used for the race itself.
        private readonly PerScreen<Horse?> competitor = new(() => null);
        // A throwaway FarmAnimal copy shown wandering the pasture (vanilla farm-animal sprite + movement).
        private readonly PerScreen<FarmAnimal?> pastureAnimal = new(() => null);
        // The most recent horse the player rode, captured before the festival dismounts them on entry.
        private readonly PerScreen<Horse?> lastRiddenMount = new(() => null);
        private readonly PerScreen<int> lastMountedTick = new(() => int.MinValue);
        private readonly PerScreen<Vector2> wanderTarget = new(() => Vector2.Zero);
        private readonly PerScreen<bool> wanderMoving = new(() => false);
        private readonly PerScreen<int> wanderDir = new(() => -1);
        private readonly PerScreen<int> wanderTicks = new(() => 0);
        private readonly PerScreen<bool> readyCheckOpen = new(() => false);
        // The player's buffs, removed for the race and restored when the festival ends.
        private readonly PerScreen<List<Buff>> suppressedBuffs = new(() => new List<Buff>());
        // Custom sprint state (independent of the vanilla buff system).
        private readonly PerScreen<SprintPhase> sprintPhase = new(() => SprintPhase.Ready);
        private readonly PerScreen<float> sprintTimer = new(() => 0f);

        private static FestivalRaceManager? Instance;

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
        private const string MsgRaceStart = "RaceStart";

        public void Initialize()
        {
            this.Helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            this.Helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            this.Helper.Events.Display.RenderedHud += this.OnRenderedHud;
            this.Helper.Events.Multiplayer.ModMessageReceived += this.OnMessageReceived;

            // Vanilla isRidingHorse() returns false during ANY event (it's gated on !Game1.eventUp), which
            // suppresses the mount drawing, riding pose and horse speed. Re-enable it while mounted inside
            // our race festival so vanilla riding works during the event.
            var harmony = new Harmony("Froshty.HorseTycoon.FestivalRace");
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.isRidingHorse)),
                postfix: new HarmonyMethod(typeof(FestivalRaceManager), nameof(IsRidingHorse_Postfix)));

            // getMovementSpeed zeroes the horse bonus / addedSpeed (which includes buff & sprint speed)
            // during events. Make it compute as non-event during our race so riding speed, the stat
            // SpeedBoost (added by JumpPatches) and the sprint buff all apply the same as outside.
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.getMovementSpeed)),
                transpiler: new HarmonyMethod(typeof(FestivalRaceManager), nameof(GetMovementSpeed_Transpiler)));

            // Block dismounting during the race so the rider can't accidentally hop off mid-run.
            // Patch checkAction (the action-button entry point that *initiates* the dismount slide),
            // not dismount() (the final cleanup) — blocking only the latter leaves the rider mid-slide.
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

        /// <summary>True while the local player is mounted inside our race festival.</summary>
        public static bool RaceRidingActive => RaceFestival != null && Game1.player?.mount != null;

        /// <summary>Treat a mounted farmer as "riding" while inside our festival, so vanilla mount rendering,
        /// riding pose and speed apply (the base method forces false during events).</summary>
        private static void IsRidingHorse_Postfix(Farmer __instance, ref bool __result)
        {
            if (!__result && __instance?.mount != null && RaceFestival != null)
                __result = true;
        }

        /// <summary>Prevent dismounting while the local player is actively racing. checkAction is the
        /// action-button handler; when already mounted it initiates the dismount slide, so swallowing it
        /// here (return true = handled, skip original) stops the dismount before it ever starts.</summary>
        private static bool CheckAction_Prefix(Horse __instance, Farmer who, ref bool __result)
        {
            if (RaceRidingActive && __instance.rider == Game1.player && who == Game1.player)
            {
                __result = true; // report "handled" so nothing else acts on the press
                return false;     // skip original checkAction (no dismount initiated)
            }
            return true;
        }

        /// <summary>Used by the getMovementSpeed transpiler: report "not in an event" during our race so the
        /// vanilla riding-speed formula (horse bonus + addedSpeed, which includes buff/sprint speed) runs.</summary>
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

            // Capture the ridden horse every tick — the festival dismounts the player as they warp in.
            if (Game1.player.isRidingHorse() && Game1.player.mount != null)
            {
                lastRiddenMount.Value = Game1.player.mount;
                lastMountedTick.Value = Game1.ticks;
            }

            Event? festival = RaceFestival;

            // Left the festival (ended/warped out) -> reset all local state.
            if (festival == null)
            {
                if (phase.Value != Phase.None)
                    this.Reset();
                return;
            }

            // Wait for the set-up event to hand control to the player before doing anything.
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
                    // animateOnce is gated on !Game1.eventUp, so advance all horses (local + remote)
                    // ourselves to restore gallop animations and their footstep sounds for every racer.
                    AdvanceHorseAnimations();
                    this.CheckFinish();
                    break;
            }

            // While mounted in the race, advance the custom sprint timer.
            if (RaceRidingActive)
                this.UpdateSprint();
        }

        /// <summary>Advance the custom sprint state (own real-time timer; the vanilla buff timer is frozen
        /// while the festival pauses game time).</summary>
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

        /// <summary>Begin a sprint (if ready), using the horse's Sprint stat for the duration.</summary>
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

        /// <summary>Save and remove all the player's buffs so only horse stats affect the race.</summary>
        private void SuppressOtherBuffs()
        {
            suppressedBuffs.Value.Clear();
            foreach (string id in Game1.player.buffs.AppliedBuffs.Keys.ToList())
            {
                suppressedBuffs.Value.Add(Game1.player.buffs.AppliedBuffs[id]);
                Game1.player.buffs.Remove(id);
            }
        }

        /// <summary>Restore the buffs that were suppressed for the race.</summary>
        private void RestoreSuppressedBuffs()
        {
            foreach (Buff buff in suppressedBuffs.Value)
                Game1.player.applyBuff(buff);
            suppressedBuffs.Value.Clear();
        }

        /// <summary>Returns the stable player-slot index for <paramref name="farmer"/> among all online farmers,
        /// sorted by UniqueMultiplayerID so every client agrees on the ordering.</summary>
        private static int PastureSlotFor(Farmer farmer) =>
            System.Math.Max(0, Game1.getOnlineFarmers()
                .OrderBy(f => f.UniqueMultiplayerID)
                .ToList()
                .IndexOf(farmer));

        /// <summary>Returns the pasture spawn tile for the given slot index.</summary>
        private static Point PastureSpawnForSlot(int slot)
        {
            Point offset = slot < PastureSlotOffsets.Length ? PastureSlotOffsets[slot] : new(slot * 4, 0);
            return new Point(PastureSpawn.X + offset.X, PastureSpawn.Y + offset.Y);
        }

        /// <summary>Bring the ridden horse into the pasture (if the player arrived mounted) and broadcast it to peers.</summary>
        private void EnterPasture(Event festival)
        {
            phase.Value = Phase.Pasture;
            competitor.Value = null;

            // Every client must set this so the event renderer draws world characters (horses placed by any client).
            festival.showWorldCharacters = true;

            Horse? horse = lastRiddenMount.Value;
            bool arrivedMounted = horse != null && (Game1.ticks - lastMountedTick.Value) <= EntryMountWindowTicks;
            if (!arrivedMounted || horse == null)
                return;

            int slot = PastureSlotFor(Game1.player);
            competitor.Value = horse;

            // Place locally and broadcast so every other client also places this horse.
            // The festival location's characters list does not net-sync, so we use explicit messaging.
            PlaceHorseInPasture(horse, slot);
            this.Helper.Multiplayer.SendMessage(
                new PastureHorseMessage(horse.HorseId.ToString(), slot),
                MsgPastureHorse,
                modIDs: new[] { this.Helper.ModRegistry.ModID });

            this.Monitor.Log($"Brought '{horse.Name}' into the festival pasture (slot {slot}).", LogLevel.Debug);
        }

        /// <summary>Handle a peer's pasture-horse broadcast: find the horse by id and place it locally.</summary>
        private void OnMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.Type == MsgRaceStart && RaceFestival != null && phase.Value == Phase.Pasture)
            {
                this.StartRace();
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

        /// <summary>Move a horse into the festival location at the given pasture slot and start its grazing animation.</summary>
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

        /// <summary>Keep the standing pasture horse's grazing animation looping.</summary>
        private void UpdatePasture()
        {
            Horse? horse = competitor.Value;
            if (horse == null)
                return;
            if (horse.Sprite?.CurrentAnimation == null)
                SetGrazingAnimation(horse);
        }

        /// <summary>Set the in-place grazing animation matching the vanilla Horse stable behaviour:
        /// transition down (21→22), graze cycles (23↔24 × 4), transition back up (22→21).</summary>
        private static void SetGrazingAnimation(Horse horse)
        {
            if (horse.Sprite == null)
                return;
            bool flip = horse.FacingDirection == Game1.left;
            horse.Sprite.loop = true;
            horse.Sprite.setCurrentAnimation(new List<FarmerSprite.AnimationFrame>
            {
                new FarmerSprite.AnimationFrame(7, Game1.random.Next(1000, 3200), secondaryArm: false, flip: flip), // idle standing
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

        /// <summary>Advance every festival horse's animation (grazing in the pasture, gallop while ridden) for
        /// local AND remote racers. animateOnce is eventUp-gated so it's frozen for everyone otherwise; the
        /// animations themselves are set by Horse.update (which runs for all mounts via updateCommon).</summary>
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

        /// <summary>The buff HUD is suppressed during events (drawHUD is skipped), so redraw it ourselves so
        /// the Sprint/Exhausted buff icons are visible during the festival race.</summary>
        /// <summary>Draw a vanilla buff-style icon (top-right) for the custom sprint/exhausted state during
        /// the race, with the time remaining shown under it and on hover.</summary>
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (!RaceRidingActive || sprintPhase.Value == SprintPhase.Ready)
                return;

            bool sprinting = sprintPhase.Value == SprintPhase.Sprinting;
            // Same icons the vanilla buffs used: Speed=9 (sprint), 25 (exhausted), in Game1.buffsIcons.
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

            // Custom sprint while racing (ModEntry's vanilla sprint is skipped during the race).
            if (RaceRidingActive && e.Button == SButton.R)
            {
                this.TryStartSprint();
                return;
            }

            if (phase.Value != Phase.Pasture || readyCheckOpen.Value)
                return;
            if (Game1.activeClickableMenu != null) // a dialogue/menu is already up
                return;
            if (!e.Button.IsActionButton())
                return;

            Event? festival = RaceFestival;
            NPC? lewis = festival?.getActorByName("Lewis");
            if (lewis == null)
                return;

            // Only react when the player is right next to Lewis.
            if (Vector2.Distance(Game1.player.Tile, lewis.Tile) > 2f)
                return;

            this.Helper.Input.Suppress(e.Button);

            if (competitor.Value == null)
            {
                Game1.drawObjectDialogue("Lewis: Come back riding your horse if you want to race!");
                return;
            }

            // Lewis asks "Ready to start the race?" (yes/no), like the vanilla festival hosts.
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

        /// <summary>After the player agrees to race: sync race start across players (MP) then run it.</summary>
        private void BeginRace()
        {
            if (!Game1.IsMultiplayer)
            {
                this.StartRace();
                return;
            }

            // MP: ReadyCheckDialog manages Game1.netReady and fires onConfirm once everyone is ready.
            readyCheckOpen.Value = true;
            Game1.netReady.SetLocalReady(ReadyCheckName, ready: true);
            Game1.activeClickableMenu = new ReadyCheckDialog(
                ReadyCheckName,
                allowCancel: true,
                onConfirm: (_) =>
                {
                    Game1.exitActiveMenu();
                    readyCheckOpen.Value = false;
                    this.StartRace();
                    this.Helper.Multiplayer.SendMessage(true, MsgRaceStart,
                        modIDs: new[] { this.Helper.ModRegistry.ModID });
                },
                onCancel: (_) =>
                {
                    Game1.netReady.SetLocalReady(ReadyCheckName, ready: false);
                    readyCheckOpen.Value = false;
                });
        }

        /// <summary>Teleport this client's player + horse to the start lane, auto-mount, and run the countdown.</summary>
        private void StartRace()
        {
            Horse? horse = competitor.Value;
            GameLocation loc = Game1.currentLocation;
            if (horse == null || loc == null)
                return;

            phase.Value = Phase.Racing;

            // Remove the player's existing buffs for the duration of the race (restored when they leave),
            // so only horse stats + the Sprint buff affect it.
            this.SuppressOtherBuffs();

            // Offset each online player's start lane — sorted by UniqueMultiplayerID so every client agrees.
            int lane = PastureSlotFor(Game1.player);
            Point start = new(RaceStart.X + lane * 2, RaceStart.Y);

            // Remove the decorative pasture FarmAnimal.
            if (pastureAnimal.Value != null)
            {
                loc.animals.Remove(pastureAnimal.Value.myID.Value);
                pastureAnimal.Value = null;
            }

            // Bring the real mountable Horse into the festival for the race (drawn via showWorldCharacters).
            horse.rider = null;
            horse.dismounting.Value = false;
            horse.mounting.Value = false;
            horse.controller = null;
            horse.currentLocation?.characters.Remove(horse);
            horse.currentLocation = loc;
            horse.Halt();
            horse.Position = TileToPixels(start);
            if (!loc.characters.Contains(horse))
                loc.characters.Add(horse);

            Game1.player.Halt();
            Game1.player.completelyStopAnimatingOrDoingAction();
            Game1.player.Position = TileToPixels(new Point(start.X, start.Y - 1));
            Game1.player.faceDirection(Game1.down);
            Game1.player.canMove = true;

            // Mount directly without the NetMutex in checkAction — the async server round-trip can
            // silently fail during festival events. Setting rider + mounting.Value = true is enough:
            // Horse.update (which runs every tick via updateCharacters even during the festival) will
            // finalize Game1.player.mount = horse and the riding pose on the next tick.
            horse.rider = Game1.player;
            horse.mounting.Value = true;
            Game1.player.synchronizedJump(6f);
            Game1.player.freezePause = 5000;
            Game1.player.Halt();
            Game1.player.UsingTool = false;

            // Let the mount hop finish before the countdown box freezes the player.
            DelayedAction.functionAfterDelay(
                () => Game1.drawObjectDialogue("3 . . . 2 . . . 1 . . . Race!"), 700);
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
        }

        private void Reset()
        {
            this.RestoreSuppressedBuffs();
            sprintPhase.Value = SprintPhase.Ready;
            sprintTimer.Value = 0f;
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
        }

        private static Vector2 TileToPixels(Point tile) => new(tile.X * 64f, tile.Y * 64f);
    }
}
