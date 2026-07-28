using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon
{
    public class JumpManager
    {
        // Internal tools
        public readonly IModHelper Helper;
        public readonly IMonitor Monitor;
        public readonly IManifest Manifest;
        public ModConfig Config = null!;

        public Texture2D? HorseShadow { get; set; }

        // State Tracking
        private readonly PerScreen<float> velX = new();
        private readonly PerScreen<float> velY = new();
        private readonly PerScreen<bool> playerJumpingWithHorse = new();
        private readonly PerScreen<bool> blockedJump = new();
        // Forward-jump trajectory state. We drive the player along an absolute path from the jump start so
        // held-direction input movement can't add to the distance, and cap it at the target tile.
        private readonly PerScreen<float> jumpDistanceRemaining = new(() => 0f);
        private readonly PerScreen<Vector2> jumpStartPos = new(() => Vector2.Zero);
        private readonly PerScreen<Vector2> jumpOffset = new(() => Vector2.Zero);
        private readonly PerScreen<bool> isForwardJump = new(() => false);
        internal float JumpDistanceRemaining { get => jumpDistanceRemaining.Value; set => jumpDistanceRemaining.Value = value; }
        internal Vector2 JumpStartPos { get => jumpStartPos.Value; set => jumpStartPos.Value = value; }
        internal Vector2 JumpOffset { get => jumpOffset.Value; set => jumpOffset.Value = value; }
        internal bool IsForwardJump { get => isForwardJump.Value; set => isForwardJump.Value = value; }

        // Properties
        internal float VelX { get => velX.Value; set => velX.Value = value; }
        internal float VelY { get => velY.Value; set => velY.Value = value; }
        internal bool PlayerJumpingWithHorse { get => playerJumpingWithHorse.Value; set => playerJumpingWithHorse.Value = value; }
        internal bool BlockedJump { get => blockedJump.Value; set => blockedJump.Value = value; }

        private readonly PerScreen<bool> gettingLocalPositionForShadow = new(() => false);

        private readonly PerScreen<int> dailyJumpCount = new(() => 0);

        public int DailyJumpCount
        {
            get => dailyJumpCount.Value;
            set => dailyJumpCount.Value = value;
        }

        internal bool GettingLocalPositionForShadow
        {
            get => gettingLocalPositionForShadow.Value;
            set => gettingLocalPositionForShadow.Value = value;
        }

        public JumpManager(IModHelper helper, IMonitor monitor, IManifest manifest)
        {
            this.Helper = helper;
            this.Monitor = monitor;
            this.Manifest = manifest;
        }

        public void Initialize()
        {
            this.HorseShadow = this.Helper.ModContent.Load<Texture2D>(Path.Combine("assets", "horse_shadow.png"));
            this.Config = this.Helper.ReadConfig<ModConfig>();

            // Hook Events
            this.Helper.Events.Input.ButtonPressed += OnButtonPressed;
            this.Helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            // Per-screen daily reset. Per-horse DailyJumps counters are reset host-side by
            // TrainingManager.ResetDailyCounters (see ModEntry.OnDayStarted).
            this.Helper.Events.GameLoop.DayStarted += (s, e) => this.DailyJumpCount = 0;

            JumpPatches.Initialize(this);
            JumpLogic.Initialize(this);
            TrainingManager.Initialize(this);

            // Load Harmony patches
            try
            {
                Harmony harmony = new(this.Manifest.UniqueID);

                // Patch for Horse Drawing
                harmony.Patch(
                    original: AccessTools.Method(typeof(Horse), nameof(Horse.draw), new Type[] { typeof(SpriteBatch) }),
                    prefix: new HarmonyMethod(typeof(JumpPatches), nameof(JumpPatches.Horse_draw_Prefix))
                );

                // Patch for Local Position
                harmony.Patch(
                    original: AccessTools.Method(typeof(Character), nameof(Character.getLocalPosition), new Type[] { typeof(xTile.Dimensions.Rectangle) }),
                    postfix: new HarmonyMethod(typeof(JumpPatches), nameof(JumpPatches.Character_getLocalPosition_Postfix))
                );

                // Patch for Draw Layer
                harmony.Patch(
                    original: AccessTools.Method(typeof(Farmer), nameof(Farmer.getDrawLayer)),
                    prefix: new HarmonyMethod(typeof(JumpPatches), nameof(JumpPatches.Farmer_getDrawLayer_Prefix))
                );

                // Patch for updating horse speed
                harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.getMovementSpeed)),
                postfix: new HarmonyMethod(typeof(JumpPatches), nameof(JumpPatches.Farmer_getMovementSpeed_Postfix))
                );
            }
            catch (Exception e)
            {
                this.Monitor.Log($"Issue with Harmony patching: {e}", LogLevel.Error);
            }

        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (FestivalRaceManager.IsStartCountdownActive)
                return;

            // Allow jumping during the Horse Festival race even though the player isn't "free" (event is up).
            bool canControl = Context.IsPlayerFree || FestivalRaceManager.RaceRidingActive;
            if (!canControl || Game1.player.IsSitting() || Game1.player.swimming.Value || Game1.currentMinigame is not null || Game1.player.yJumpVelocity != 0 || !Game1.player.isRidingHorse())
                return;

            if (e.Button == Config?.JumpButton)
            {
                JumpLogic.TryToJump();
            }
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // The jump is over when the synchronized jump has fully landed (velocity and offset
            // both zero; synchronizedJump sets them non-zero synchronously, so this can't trigger
            // on the subscribe tick) or was cancelled externally (dismount, warp, or anything
            // calling completelyStopAnimating zeroes both mid-air). Checking only the landing
            // descent (velocity crossing negative → zero) missed the cancel case, leaving this
            // handler subscribed forever and rewriting the player's position every tick, so remote
            // clients then saw the horse gallop in place whenever this player stood still mounted.
            bool jumpOver = Game1.player.yJumpVelocity == 0f && Game1.player.yJumpOffset == 0;
            if (jumpOver || Game1.player.mount == null)
            {
                PlayerJumpingWithHorse = false;
                BlockedJump = false;
                if (IsForwardJump)
                    Game1.player.canMove = true;
                IsForwardJump = false;
                this.Helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
                return;
            }

            if (IsForwardJump)
            {
                // Drive the player along an ABSOLUTE path from the jump start. Using += let held-direction
                // input movement stack onto the jump (overshoot when clearing objects); setting the position
                // outright ignores that drift. Cap the travel at the target tile regardless of airtime.
                float vx = VelX;
                float vy = VelY;
                float step = (float)Math.Sqrt(vx * vx + vy * vy);
                if (JumpDistanceRemaining > 0f && step > 0f)
                {
                    if (step >= JumpDistanceRemaining)
                    {
                        float scale = JumpDistanceRemaining / step;
                        vx *= scale;
                        vy *= scale;
                        JumpDistanceRemaining = 0f;
                    }
                    else
                    {
                        JumpDistanceRemaining -= step;
                    }
                    JumpOffset = new Vector2(JumpOffset.X + vx, JumpOffset.Y + vy);
                }
                Game1.player.Position = JumpStartPos + JumpOffset;
            }
            else
            {
                Game1.player.position.X += VelX;
                Game1.player.position.Y += VelY;
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var configMenu = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu is null)
                return;

            configMenu.Register(
                mod: this.Manifest,
                reset: () => Config = new ModConfig(),
                save: () => this.Helper.WriteConfig(Config)
            );
        }

        public void SubscribeToUpdate()
        {
            this.Helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        }
    }
}