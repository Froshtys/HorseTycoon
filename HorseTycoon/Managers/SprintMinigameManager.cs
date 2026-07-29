using HorseTycoon.Models;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace HorseTycoon
{
    /// <summary>
    /// The timing-bar sprint (<see cref="ModConfig.UseSprintMinigame"/>). Pressing the sprint key starts a
    /// sprint at <see cref="HorseStats.MinigameBaseSpeedBonus"/> and opens a horizontal meter styled after the
    /// fishing minigame: a green bar sweeps left to right and wraps, and each press landing it on the centre
    /// marker stacks another <see cref="HorseStats.MinigameHitSpeedBonus"/> of speed. A miss costs an attempt
    /// but never speed. When the attempts run out the horse holds its final speed briefly, then tires.
    ///
    /// This replaces both legacy sprints at once. It is driven off real elapsed milliseconds and never touches
    /// vanilla <see cref="StardewValley.Buffs.Buff"/>s, so unlike the old overworld sprint it keeps working
    /// during the festival races, where the game clock (and therefore every buff timer) is frozen.
    ///
    /// State is per-screen and purely local, matching the sprint it replaces: nothing is synced to other players.
    /// </summary>
    internal static class SprintMinigameManager
    {
        private enum Phase
        {
            /// <summary>Not sprinting; the sprint key is available.</summary>
            Idle,
            /// <summary>Sprinting with the meter open and attempts remaining.</summary>
            Running,
            /// <summary>Attempts spent; coasting at the final speed before the sprint drops.</summary>
            Coasting,
            /// <summary>Cooling down; the sprint key does nothing.</summary>
            Exhausted
        }

        // --- Tuning ---
        /// <summary>How long the horse holds its final speed after the last attempt is spent.</summary>
        private const float CoastMs = 1000f;
        /// <summary>Cooldown before the next sprint, matching the legacy Exhausted debuff.</summary>
        private const float ExhaustMs = 10000f;
        /// <summary>How long the meter flashes green on a hit or red on a miss.</summary>
        private const float FlashMs = 200f;

        // --- Meter geometry, in source pixels of the vanilla fishing bar art (see BobberBar.draw) ---
        private const int Scale = 3;
        /// <summary>The fishing bar's wooden trough. Vanilla's frame rect starts at x=644, but its first ten
        /// columns are the fishing rod and reel, which make no sense on a horse; start at 654 to crop them off.</summary>
        private static readonly Rectangle FrameSrc = new(654, 1999, 28, 150);
        private static readonly Rectangle BarStartSrc = new(682, 2078, 9, 2);
        private static readonly Rectangle BarMiddleSrc = new(682, 2081, 9, 1);
        private static readonly Rectangle BarEndSrc = new(682, 2085, 9, 2);
        /// <summary>Offset of the usable channel from the start of the frame, along its long axis.</summary>
        private const int ChannelStart = 3;
        /// <summary>Length of the usable channel along the frame's long axis.</summary>
        private const int ChannelLength = 142;
        /// <summary>Gap between the frame's far edge and the channel, across its short axis. Measured from the
        /// far edge so cropping the rod off the near edge doesn't shift it.</summary>
        private const int ChannelFarOffset = 13;
        /// <summary>Gap between the frame's far edge and the attempt notches, across its short axis.</summary>
        private const int NotchFarOffset = 3;
        /// <summary>Thickness of the attempt notches, across the frame's short axis.</summary>
        private const int NotchThickness = 6;

        // --- Buff icon, placed in vanilla's buff row rather than the screen corner ---
        private const int BuffIconSize = 64;
        private const int BuffIconGap = 8;
        /// <summary>Fallback only, for the vanishingly unlikely case that Game1.buffsDisplay isn't built yet.
        /// Mirrors BuffsDisplay.updatePosition: a 288px-wide box inset 300px from the title-safe right edge,
        /// which is the reservation that keeps buffs clear of the clock and money box.</summary>
        private const int VanillaBuffRowFallbackInset = 300 + 288;

        private static IModHelper Helper = null!;
        private static IMonitor Monitor = null!;
        private static Texture2D? sprintBuffIcon;

        // --- Per-screen state (split-screen safe, like the rest of this mod) ---
        private static readonly PerScreen<Phase> phase = new(() => Phase.Idle);
        /// <summary>Current additive speed bonus, in getMovementSpeed units.</summary>
        private static readonly PerScreen<float> speedBonus = new(() => 0f);
        /// <summary>Centre of the green bar as a fraction of the channel.</summary>
        private static readonly PerScreen<float> sweepCentre = new(() => 0f);
        /// <summary>Half the green bar's width as a fraction of the channel; this *is* the hit window.</summary>
        private static readonly PerScreen<float> windowHalf = new(() => 0f);
        /// <summary>Time for one pass of the bar, fixed at sprint start from the horse's Sprint stat.</summary>
        private static readonly PerScreen<float> sweepMs = new(() => 1500f);
        private static readonly PerScreen<int> chancesLeft = new(() => 0);
        private static readonly PerScreen<int> chancesTotal = new(() => 0);
        /// <summary>Counts down the Coasting and Exhausted phases.</summary>
        private static readonly PerScreen<float> phaseTimer = new(() => 0f);
        /// <summary>Counts down the post-attempt flash on the meter.</summary>
        private static readonly PerScreen<float> flashMs = new(() => 0f);
        /// <summary>Whether the flash currently running is a hit (green) or a miss (red).</summary>
        private static readonly PerScreen<bool> flashWasHit = new(() => false);

        /// <summary>Whether the local player's horse is currently getting the minigame speed bonus.</summary>
        public static bool IsSprinting =>
            phase.Value is Phase.Running or Phase.Coasting;

        /// <summary>The local player's current additive sprint speed bonus, or 0 when not sprinting.</summary>
        public static float CurrentSpeedBonus =>
            IsSprinting ? speedBonus.Value : 0f;

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            Helper = helper;
            Monitor = monitor;
            sprintBuffIcon = helper.ModContent.Load<Texture2D>("assets/HorseRunningBuff.png");

            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Display.RenderedHud += OnRenderedHud;

            helper.ConsoleCommands.Add("sprint_mode",
                "Switches between the timing-bar sprint minigame and the classic flat sprint buff.\n\n"
                + "Usage: sprint_mode [minigame|classic]\n"
                + "- Omit the argument to report the current mode.\n"
                + $"- Also bound to {ModEntry.Config.SprintModeToggleButton} in-game (ModConfig.SprintModeToggleButton).",
                HandleSprintModeCommand);
        }

        private static void HandleSprintModeCommand(string command, string[] args)
        {
            if (args.Length == 0)
            {
                Monitor.Log($"Sprint mode is currently: {DescribeMode(ModEntry.Config.UseSprintMinigame)}.", LogLevel.Info);
                return;
            }

            bool minigame;
            switch (args[0].ToLower())
            {
                case "minigame":
                case "on":
                    minigame = true;
                    break;
                case "classic":
                case "off":
                    minigame = false;
                    break;
                default:
                    Monitor.Log($"Unknown mode '{args[0]}'. Use 'minigame' or 'classic'.", LogLevel.Warn);
                    return;
            }

            SetSprintMode(minigame);
            Monitor.Log($"Sprint mode set to: {DescribeMode(minigame)}.", LogLevel.Info);
        }

        private static string DescribeMode(bool minigame) =>
            minigame ? "minigame (timing bar)" : "classic (flat sprint buff)";

        /*********
        ** Input
        *********/
        private static void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (e.Button == ModEntry.Config.SprintModeToggleButton)
            {
                ToggleSprintMode();
                return;
            }

            if (!ModEntry.Config.UseSprintMinigame)
                return;

            if (e.Button != SButton.LeftShift && e.Button != SButton.RightShift)
                return;

            // The festival swallows sprint presses during the start countdown so the rider doesn't smash
            // their way out of the starting stall; respect that here too.
            if (FestivalRaceManager.IsStartCountdownActive)
                return;

            if (Game1.activeClickableMenu != null || Game1.player.mount == null)
                return;

            switch (phase.Value)
            {
                case Phase.Idle:
                    StartSprint();
                    break;

                case Phase.Running:
                    TakeAttempt();
                    break;

                // Coasting and Exhausted ignore the key: the sprint is already committed.
            }
        }

        private static void ToggleSprintMode() =>
            SetSprintMode(!ModEntry.Config.UseSprintMinigame);

        /// <summary>Switches sprint systems and persists the choice, cancelling any sprint in flight.</summary>
        private static void SetSprintMode(bool useMinigame)
        {
            ModEntry.Config.UseSprintMinigame = useMinigame;
            Helper.WriteConfig(ModEntry.Config);

            // Don't leave a half-played meter (or its speed bonus) behind when switching systems.
            Reset();

            if (Context.IsWorldReady)
                Game1.addHUDMessage(new HUDMessage($"Sprint: {(useMinigame ? "minigame" : "classic")}", HUDMessage.newQuest_type));
        }

        /*********
        ** Sprint lifecycle
        *********/
        private static void StartSprint()
        {
            var mount = Game1.player.mount;
            if (mount == null)
                return;

            var (_, totalSprint) = HorseHelper.GetRaceStats(mount);
            var (sprintIV, sprintEV) = HorseHelper.GetSprintIVEV(mount);

            phase.Value = Phase.Running;
            speedBonus.Value = HorseStats.MinigameBaseSpeedBonus;
            chancesTotal.Value = chancesLeft.Value = HorseStats.MinigameChances(totalSprint);
            windowHalf.Value = HorseStats.MinigameWindowHalf(sprintIV, sprintEV);
            sweepMs.Value = HorseStats.MinigameSweepMs(chancesTotal.Value);
            sweepCentre.Value = windowHalf.Value;
            phaseTimer.Value = 0f;
            flashMs.Value = 0f;

            Logger.LogVerbose($"Sprint (minigame): sprint={totalSprint} (IV {sprintIV}/EV {sprintEV}), " +
                $"chances={chancesTotal.Value}, sweep={sweepMs.Value}ms, window=±{windowHalf.Value:0.###}, " +
                $"base=+{speedBonus.Value:0.##}");

            Game1.playSound("fireball");

            // Training credits one sprint per sprint, not per timing hit, so this stays where the legacy
            // sprints called it: at the moment the sprint begins.
            TrainingManager.ProcessSprint(mount);
        }

        /// <summary>Spends one attempt on a press, awarding speed if the bar is over the centre marker.</summary>
        private static void TakeAttempt()
        {
            bool hit = System.Math.Abs(sweepCentre.Value - 0.5f) <= windowHalf.Value;
            if (hit)
                speedBonus.Value += HorseStats.MinigameHitSpeedBonus;

            SpendChance(hit, hit ? "hit" : "mistimed");
        }

        /// <summary>
        /// Consumes one attempt and gives the feedback for it, ending the sprint once they run out.
        /// Both ways of missing — a mistimed press and a sweep running out unpressed — come through here,
        /// so they look and sound the same to the player.
        /// </summary>
        private static void SpendChance(bool hit, string reason)
        {
            chancesLeft.Value--;
            sweepCentre.Value = windowHalf.Value;

            flashMs.Value = FlashMs;
            flashWasHit.Value = hit;
            Game1.playSound(hit ? "crit" : "cancel");

            Logger.LogVerbose($"Sprint (minigame): {reason}, speed=+{speedBonus.Value:0.##}, chances left={chancesLeft.Value}");

            if (chancesLeft.Value <= 0)
            {
                phase.Value = Phase.Coasting;
                phaseTimer.Value = CoastMs;
            }
        }

        private static void BeginExhaustion()
        {
            phase.Value = Phase.Exhausted;
            phaseTimer.Value = ExhaustMs;
            speedBonus.Value = 0f;
        }

        private static void Reset()
        {
            phase.Value = Phase.Idle;
            speedBonus.Value = 0f;
            phaseTimer.Value = 0f;
            chancesLeft.Value = chancesTotal.Value = 0;
            flashMs.Value = 0f;
        }

        /*********
        ** Tick
        *********/
        private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (phase.Value == Phase.Idle)
                return;

            if (!Context.IsWorldReady || Game1.currentGameTime == null)
            {
                Reset();
                return;
            }

            float elapsed = Game1.currentGameTime.ElapsedGameTime.Milliseconds;
            if (flashMs.Value > 0f)
                flashMs.Value -= elapsed;

            // Dismounting mid-sprint drops the sprint and tires the horse, as the legacy sprint did.
            if (Game1.player.mount == null && IsSprinting)
            {
                BeginExhaustion();
                return;
            }

            switch (phase.Value)
            {
                case Phase.Running:
                    // Freeze the sweep behind a menu so opening the inventory can't burn every attempt.
                    if (Game1.activeClickableMenu == null)
                        AdvanceSweep(elapsed);
                    break;

                case Phase.Coasting:
                    phaseTimer.Value -= elapsed;
                    if (phaseTimer.Value <= 0f)
                        BeginExhaustion();
                    break;

                case Phase.Exhausted:
                    phaseTimer.Value -= elapsed;
                    if (phaseTimer.Value <= 0f)
                        Reset();
                    break;
            }
        }

        /// <summary>Moves the green bar along the track, spending an attempt if it runs off the end unpressed.</summary>
        private static void AdvanceSweep(float elapsedMs)
        {
            // The bar's centre travels between the two points where it just touches each end of the
            // channel, so a wide (easy) bar never overhangs the frame.
            float travel = 1f - (2f * windowHalf.Value);
            sweepCentre.Value += travel * (elapsedMs / sweepMs.Value);

            if (sweepCentre.Value > 1f - windowHalf.Value)
                SpendChance(hit: false, "sweep expired");
        }

        /*********
        ** HUD
        *********/
        private static void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (phase.Value == Phase.Idle || !Context.IsWorldReady)
                return;

            SpriteBatch b = e.SpriteBatch;

            DrawBuffIcon(b);

            // The exhaustion cooldown is conveyed entirely by the corner debuff icon; only the active
            // sprint puts the meter on screen.
            if (phase.Value == Phase.Exhausted)
                return;

            int trackLength = FrameSrc.Height * Scale;
            int trackThickness = FrameSrc.Width * Scale;
            int left = (Game1.uiViewport.Width - trackLength) / 2;
            int top = Game1.uiViewport.Height - trackThickness - 140;

            DrawTrack(b, left, top);
            if (phase.Value == Phase.Running)
            {
                DrawSweepBar(b, left, top);
                DrawCentreMarker(b, left, top, trackThickness);
            }
            DrawReadout(b, left, top, trackLength, trackThickness);
        }

        /// <summary>
        /// Draws the sprint buff (or exhaustion debuff) in the top-right buff stack, matching where the
        /// festival race puts its own icons. The minigame can't use a vanilla <see cref="StardewValley.Buffs.Buff"/>
        /// for this — its timer would freeze during a race — so the icon is drawn by hand.
        /// </summary>
        private static void DrawBuffIcon(SpriteBatch b)
        {
            bool exhausted = phase.Value == Phase.Exhausted;

            // Sit just left of vanilla's whole buff box rather than trying to reproduce its internal slot
            // layout. Vanilla packs its icons right-to-left inside that box, so clearing the box's left
            // edge keeps us off both the buffs and the clock no matter how many buffs are up. Reading its
            // own position also means we inherit whatever resolution and UI scale it resolved to.
            var buffBox = Game1.buffsDisplay;
            int x = (buffBox?.xPositionOnScreen ?? (Utility.getSafeArea().Right - VanillaBuffRowFallbackInset))
                - BuffIconSize - BuffIconGap;
            int y = buffBox?.yPositionOnScreen ?? (Utility.getSafeArea().Top + 8);

            string label;
            string caption;
            if (exhausted)
            {
                int secondsLeft = (int)System.Math.Ceiling(phaseTimer.Value / 1000f);
                label = "Horse Exhausted\nYour horse needs a break before another sprint!";
                caption = $"{secondsLeft}s";

                // Vanilla buff sheet index 25 is the red "sick" icon the classic Exhausted debuff uses too.
                Rectangle src = Game1.getSourceRectForStandardTileSheet(Game1.buffsIcons, 25, 16, 16);
                b.Draw(Game1.buffsIcons, new Vector2(x, y), src, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            }
            else
            {
                label = $"Horse Sprint (+{speedBonus.Value:0.##} speed)"
                    + (phase.Value == Phase.Running
                        ? $"\n{chancesLeft.Value} of {chancesTotal.Value} attempts left"
                        : "");
                caption = $"+{speedBonus.Value:0.##}";

                if (sprintBuffIcon != null)
                    b.Draw(sprintBuffIcon, new Rectangle(x, y, BuffIconSize, BuffIconSize), Color.White);
            }

            Vector2 size = Game1.smallFont.MeasureString(caption);
            Utility.drawTextWithShadow(b, caption, Game1.smallFont,
                new Vector2(x + ((BuffIconSize - size.X) / 2f), y + BuffIconSize - 8f), Color.White);

            if (new Rectangle(x, y, BuffIconSize, BuffIconSize).Contains(Game1.getOldMouseX(), Game1.getOldMouseY()))
                IClickableMenu.drawHoverText(b, label, Game1.smallFont);
        }

        private static void DrawTrack(SpriteBatch b, int left, int top)
        {
            Color tint = flashMs.Value <= 0f
                ? Color.White
                : (flashWasHit.Value ? Color.LightGreen : Color.Salmon);
            DrawRotated(b, Game1.mouseCursors, FrameSrc, left, top, Scale, Scale, tint, 0.85f);
        }

        private static void DrawSweepBar(SpriteBatch b, int left, int top)
        {
            // Half the bar's width is the hit window, so the green block the player sees is exactly the
            // target they have to land on the marker.
            float barLength = System.Math.Max(6f, 2f * windowHalf.Value * ChannelLength);
            float barStart = ChannelStart + ((sweepCentre.Value * ChannelLength) - (barLength / 2f));

            int barTop = top + (ChannelFarOffset * Scale);
            float barLeft = left + (barStart * Scale);
            float capLength = BarStartSrc.Height * Scale;
            float middleLength = (barLength * Scale) - (2f * capLength);

            DrawRotated(b, Game1.mouseCursors, BarStartSrc, barLeft, barTop, Scale, Scale, Color.White, 0.86f);
            if (middleLength > 0f)
            {
                DrawRotated(b, Game1.mouseCursors, BarMiddleSrc, barLeft + capLength, barTop,
                    Scale, middleLength, Color.White, 0.86f);
            }
            DrawRotated(b, Game1.mouseCursors, BarEndSrc, barLeft + capLength + System.Math.Max(0f, middleLength), barTop,
                Scale, Scale, Color.White, 0.86f);
        }

        private static void DrawCentreMarker(SpriteBatch b, int left, int top, int trackThickness)
        {
            int centre = left + ((ChannelStart + (ChannelLength / 2)) * Scale);
            int markerTop = top + (ChannelFarOffset * Scale) - 8;
            int markerHeight = (BarStartSrc.Width * Scale) + 16;

            b.Draw(Game1.staminaRect, new Rectangle(centre - 4, markerTop, 8, markerHeight), Color.Black * 0.7f);
            b.Draw(Game1.staminaRect, new Rectangle(centre - 2, markerTop + 2, 4, markerHeight - 4), Color.Gold);
        }

        private static void DrawReadout(SpriteBatch b, int left, int top, int trackLength, int trackThickness)
        {
            DrawAttemptNotches(b, left, top);

            string speedText = $"+{speedBonus.Value:0.##} speed";
            Vector2 size = Game1.smallFont.MeasureString(speedText);
            Utility.drawTextWithShadow(b, speedText, Game1.smallFont,
                new Vector2(left + ((trackLength - size.X) / 2f), top - size.Y - 8f), Color.White);
        }

        /// <summary>
        /// Shows the remaining attempts as notches burned into the wooden band along the top of the frame,
        /// rather than as a separate row of pips: one notch per attempt, spanning the same length as the
        /// channel below so each notch sits over the stretch of track it pays for. Spent notches darken.
        /// </summary>
        private static void DrawAttemptNotches(SpriteBatch b, int left, int top)
        {
            if (chancesTotal.Value <= 0)
                return;

            const int notchGap = 3;
            int notchTop = top + (NotchFarOffset * Scale);
            int notchHeight = NotchThickness * Scale;
            int bandLeft = left + (ChannelStart * Scale);
            int bandWidth = ChannelLength * Scale;

            for (int i = 0; i < chancesTotal.Value; i++)
            {
                // Lay the notches out from the band edges so rounding can't leave a ragged gap at the end.
                int notchLeft = bandLeft + (int)((long)bandWidth * i / chancesTotal.Value);
                int notchRight = bandLeft + (int)((long)bandWidth * (i + 1) / chancesTotal.Value);
                var rect = new Rectangle(notchLeft + notchGap, notchTop,
                    notchRight - notchLeft - (2 * notchGap), notchHeight);

                // Tint rather than fill, so the wood grain of the frame still reads through.
                b.Draw(Game1.staminaRect, rect,
                    i < chancesLeft.Value ? Color.Gold * 0.55f : Color.Black * 0.6f);
            }
        }

        /// <summary>
        /// Draws a sprite rotated a quarter turn anticlockwise, so the vanilla fishing bar's vertical art
        /// reads as a horizontal track. <paramref name="left"/> and <paramref name="top"/> are the top-left
        /// corner of the result on screen; <paramref name="acrossScale"/> scales the source width (which
        /// becomes the track's thickness) and <paramref name="alongScale"/> scales the source height (which
        /// becomes its length).
        /// </summary>
        private static void DrawRotated(SpriteBatch b, Texture2D texture, Rectangle source, float left, float top,
            float acrossScale, float alongScale, Color color, float layerDepth)
        {
            // With origin (0,0) and a -90° rotation, source pixel (u,v) lands at
            // position + (v * alongScale, -u * acrossScale), so the sprite hangs above and to the right of
            // position. Push position down by the resulting thickness to land its top-left on (left, top).
            var position = new Vector2(left, top + (source.Width * acrossScale));
            b.Draw(texture, position, source, color, -MathHelper.PiOver2, Vector2.Zero,
                new Vector2(acrossScale, alongScale), SpriteEffects.None, layerDepth);
        }
    }
}
