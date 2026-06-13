using System.Linq;
using Microsoft.Xna.Framework;
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

        // --- Tunable map coordinates (tiles) for CP.HorseTycoon_ForestFestival. Tune in-game with `ht_race_tile`. ---
        // Marnie's pasture: where the brought horse is placed and wanders.
        private static readonly Point PastureMin = new(80, 18);
        private static readonly Point PastureMax = new(90, 23);
        private static readonly Point PastureSpawn = new(85, 20);
        // Lewis is placed via Set-Up_additionalCharacters in spring21.json; the race trigger reads his
        // live actor tile, so no constant is needed here.
        // Race start "right below the lake" (first lane); extra players are offset along X.
        private static readonly Point RaceStart = new(60, 47);
        // Finish band (inclusive tile rectangle). Crossing it ends the race.
        // Finish posts are at (39,11) and (39,17), so the line is X~39 between those Y values.
        private static readonly Point FinishMin = new(38, 11);
        private static readonly Point FinishMax = new(40, 17);

        private const float WanderSpeed = 1.5f;            // px per tick
        private const int WanderRepickMinTicks = 90;        // ~1.5s
        private const int WanderRepickMaxTicks = 210;       // ~3.5s

        // The game dismounts the player as they warp into the festival, so we capture the mount each tick
        // beforehand and treat them as "arrived mounted" if they were riding within this many ticks of the
        // pasture phase beginning (covers the warp + set-up sequence; you can't remount during it).
        private const int EntryMountWindowTicks = 600; // ~10s

        private enum Phase { None, Pasture, Racing, Finished }

        private readonly IModHelper Helper;
        private readonly IMonitor Monitor;

        private readonly PerScreen<Phase> phase = new(() => Phase.None);
        private readonly PerScreen<Horse?> competitor = new(() => null);
        // The most recent horse the player rode, captured before the festival dismounts them on entry.
        private readonly PerScreen<Horse?> lastRiddenMount = new(() => null);
        private readonly PerScreen<int> lastMountedTick = new(() => int.MinValue);
        private readonly PerScreen<Vector2> wanderTarget = new(() => Vector2.Zero);
        private readonly PerScreen<int> wanderTicks = new(() => 0);
        private readonly PerScreen<int> wanderFacing = new(() => -1);
        private readonly PerScreen<bool> readyCheckOpen = new(() => false);

        public FestivalRaceManager(IModHelper helper, IMonitor monitor)
        {
            this.Helper = helper;
            this.Monitor = monitor;
        }

        public void Initialize()
        {
            this.Helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            this.Helper.Events.Input.ButtonPressed += this.OnButtonPressed;

            this.Helper.ConsoleCommands.Add(
                "ht_race_tile",
                "Logs the player's current tile (for tuning festival race coordinates).",
                (_, _) => this.Monitor.Log(
                    $"Player tile: {Game1.player.Tile} | mounted: {Game1.player.isRidingHorse()} | location: {Game1.currentLocation?.Name}",
                    LogLevel.Info));
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
                    this.UpdateWander();
                    break;
                case Phase.Racing:
                    // Keep the mount (an event actor) drawn under the rider during the festival.
                    if (competitor.Value != null && Game1.player.mount == competitor.Value)
                        competitor.Value.Position = Game1.player.Position;
                    this.CheckFinish();
                    break;
            }
        }

        /// <summary>Bring the ridden horse into the pasture (if the player arrived mounted) and start wandering.</summary>
        private void EnterPasture(Event festival)
        {
            phase.Value = Phase.Pasture;
            competitor.Value = null;

            Horse? horse = lastRiddenMount.Value;
            bool arrivedMounted = horse != null && (Game1.ticks - lastMountedTick.Value) <= EntryMountWindowTicks;
            if (!arrivedMounted || horse == null)
                return;

            // The festival dismounted the horse into the location the player came from. During a festival the
            // location's normal characters aren't drawn (Event.draw only renders event actors), so we move the
            // horse into the festival as an event actor instead.
            horse.currentLocation?.characters.Remove(horse);
            horse.rider = null;
            horse.dismounting.Value = false;
            horse.mounting.Value = false;
            horse.currentLocation = Game1.currentLocation;
            horse.EventActor = true;
            horse.Position = TileToPixels(PastureSpawn);
            if (!festival.actors.Contains(horse))
                festival.actors.Add(horse);

            competitor.Value = horse;
            wanderTicks.Value = 0; // pick a wander target immediately
            this.Monitor.Log($"Brought '{horse.Name}' into the festival pasture.", LogLevel.Debug);
        }

        /// <summary>Simple bounded wander so the brought horse walks around the pasture (visual only).</summary>
        private void UpdateWander()
        {
            Horse? horse = competitor.Value;
            if (horse == null)
                return;

            wanderTicks.Value--;
            Vector2 pos = horse.Position;

            if (wanderTicks.Value <= 0 || Vector2.Distance(pos, wanderTarget.Value) < 6f)
            {
                int tx = Game1.random.Next(PastureMin.X, PastureMax.X + 1);
                int ty = Game1.random.Next(PastureMin.Y, PastureMax.Y + 1);
                wanderTarget.Value = TileToPixels(new Point(tx, ty));
                wanderTicks.Value = Game1.random.Next(WanderRepickMinTicks, WanderRepickMaxTicks);
                return;
            }

            Vector2 dir = wanderTarget.Value - pos;
            if (dir.LengthSquared() > 0.001f)
            {
                dir.Normalize();
                horse.Position = pos + dir * WanderSpeed;

                // Only call faceDirection when the direction CHANGES — it resets the sprite to the standing
                // frame, so calling it every tick fights animateInFacingDirection and causes flicker.
                int facing = System.Math.Abs(dir.X) > System.Math.Abs(dir.Y)
                    ? (dir.X > 0 ? Game1.right : Game1.left)
                    : (dir.Y > 0 ? Game1.down : Game1.up);
                if (facing != wanderFacing.Value)
                {
                    horse.faceDirection(facing);
                    wanderFacing.Value = facing;
                }
                horse.animateInFacingDirection(Game1.currentGameTime);
            }
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || phase.Value != Phase.Pasture || readyCheckOpen.Value)
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

            // Offset each online player's start lane so racers don't stack.
            int lane = Game1.getOnlineFarmers().ToList().IndexOf(Game1.player);
            if (lane < 0) lane = 0;
            Point start = new(RaceStart.X + lane * 2, RaceStart.Y);

            Game1.player.Halt();
            Game1.player.completelyStopAnimatingOrDoingAction();
            Game1.player.faceDirection(Game1.down);

            // Finalize the mounted state directly. The animated mount (Horse.checkAction) relies on
            // Horse.update setting rider.mount, which doesn't run for an event-actor horse — so the mount
            // never completes and the rider shows the running animation. Set it explicitly instead.
            horse.mounting.Value = false;
            horse.dismounting.Value = false;
            horse.rider = Game1.player;
            Game1.player.mount = horse;
            Game1.player.isAnimatingMount = false;
            Game1.player.freezePause = 0;
            Game1.player.canMove = true;
            horse.Position = TileToPixels(start);
            Game1.player.Position = TileToPixels(start);

            Game1.drawObjectDialogue("3 . . . 2 . . . 1 . . . Race!");
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
            phase.Value = Phase.None;
            competitor.Value = null;
            wanderTicks.Value = 0;
            wanderTarget.Value = Vector2.Zero;
            readyCheckOpen.Value = false;
        }

        private static Vector2 TileToPixels(Point tile) => new(tile.X * 64f, tile.Y * 64f);
    }
}
