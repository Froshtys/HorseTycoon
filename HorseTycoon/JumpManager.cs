using System;
using System.IO;
using HarmonyLib;
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
        public ModConfig Config;

        public Texture2D HorseShadow { get; set; }

        // State Tracking
        private readonly PerScreen<float> velX = new();
        private readonly PerScreen<float> velY = new();
        private readonly PerScreen<float> lastYJumpVelocity = new();
        private readonly PerScreen<bool> playerJumpingWithHorse = new();
        private readonly PerScreen<bool> blockedJump = new();

        // Properties
        internal float VelX { get => velX.Value; set => velX.Value = value; }
        internal float VelY { get => velY.Value; set => velY.Value = value; }
        internal float LastYJumpVelocity { get => lastYJumpVelocity.Value; set => lastYJumpVelocity.Value = value; }
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
            this.Helper.Events.Content.AssetRequested += OnAssetRequested;
            this.Helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            // Add the daily reset
            this.Helper.Events.GameLoop.DayStarted += (s, e) =>
            {
                this.DailyJumpCount = 0;
                // You could also add DailySpeedDistance = 0, etc. here later
            };

            this.Helper.Events.GameLoop.DayStarted += (s, e) =>
            {
                foreach (var horse in HorseHelper.GetAllBarnHorses())
                {
                    var stats = horse.GetHorseStats();
                    // Only reset if they haven't earned the point yet
                    // If they did earn it, LastTrainDate handles the block
                    stats.DailyJumps = 0;
                }
            };

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
            }
            catch (Exception e)
            {
                this.Monitor.Log($"Issue with Harmony patching: {e}", LogLevel.Error);
            }

        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsPlayerFree || Game1.player.IsSitting() || Game1.player.swimming.Value || Game1.currentMinigame is not null || Game1.player.yJumpVelocity != 0 || !Game1.player.isRidingHorse())
                return;

            if (e.Button == Config.JumpButton)
            {
                JumpLogic.TryToJump();
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (Game1.player.yJumpVelocity == 0f && LastYJumpVelocity < 0f)
            {
                PlayerJumpingWithHorse = false;
                BlockedJump = false;
                Game1.player.canMove = true;
                this.Helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
                return;
            }
            Game1.player.position.X += VelX;
            Game1.player.position.Y += VelY;
            LastYJumpVelocity = Game1.player.yJumpVelocity;
        }

        private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {

            if (e.NameWithoutLocale.IsEquivalentTo("Animals/horse"))
            {
                e.LoadFromModFile<Texture2D>(Path.Combine("assets", "horse.png"), AssetLoadPriority.Medium);
            }
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            var configMenu = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu is null)
                return;

            configMenu.Register(
                mod: this.Manifest,
                reset: () => Config = new ModConfig(),
                save: () => this.Helper.WriteConfig(Config)
            );

            // Add GMCM options using this.Helper.Translation...
            // (Keep original GMCM code here, just replace 'ModManifest' with 'this.Manifest')
        }

        public void SubscribeToUpdate()
        {
            this.Helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        }
    }
}