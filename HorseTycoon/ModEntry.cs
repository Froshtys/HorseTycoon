using System;
using HarmonyLib;
using HorseTycoon.Menus;
using HorseTycoon.Models;
using HorseTycoon.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Buffs;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Menus;
using static HorseTycoon.Models.HorseStats;

namespace HorseTycoon
{

    /// <summary>The mod entry point.</summary>
    internal sealed class ModEntry : Mod
    {
        public override void Entry(IModHelper helper)
        {
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;

            var harmony = new Harmony(this.ModManifest.UniqueID);
            FarmAnimalPatches.Apply(harmony);

            MenuPatches.Initialize(helper, this.Monitor);
            // 3. Apply the patches
            MenuPatches.Apply(harmony);
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {

        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            Utility.ForEachLocation(location =>
            {
                foreach (FarmAnimal animal in location.animals.Values)
                {
                    // Only target horses
                    if (animal.type.Value.Contains("Horse"))
                    {
                        var stats = animal.GetHorseStats();

                        // Check if the horse is "New" (no SpeedIV set yet)
                        if (!animal.modData.ContainsKey(HorseStats.SpeedIVKey))
                        {
                            this.Monitor.Log($"Initializing stats for new horse: {animal.Name}", LogLevel.Debug);
                            // Initialize with Starter quality
                            stats.RandomizeStats(HorseSourceQuality.Starter);
                            HorseHelper.LogHorseData(animal, this.Monitor);
                        }
                    }
                }
                return true; // Continue to next location
            });

        }

        private void SetHorseSkin(Horse horse, string variation)
        {
            const string AlternativeTextureOwner = "Froshty.HorseTycoonAT";
            const string AlternativeTextureName = "Froshty.HorseTycoonAT.Character_Horse";

            horse.modData["AlternativeTextureOwner"] = AlternativeTextureOwner;
            horse.modData["AlternativeTextureName"] = AlternativeTextureName;
            horse.modData["AlternativeTextureVariation"] = variation;

            foreach (var key in horse.modData.Keys)
            {
                {
                    this.Monitor.Log($"Horse ModData Key: {key} | Value: {horse.modData[key]}", LogLevel.Info);
                }
            }

        }


        /*********
        ** Private methods
        *********/
        /// <summary>Raised after the player presses a button on the keyboard, controller, or mouse.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            // ignore if player hasn't loaded a save yet
            if (!Context.IsWorldReady)
                return;

            processHorseSprint(sender, e);

            // Only trigger on Left-Click
            if (!e.Button.IsUseToolButton()) return;

            if (Game1.player.CurrentItem is StardewValley.Tools.WateringCan)
                return;

            Vector2 tile = e.Cursor.GrabTile;
            Building building = Game1.currentLocation.getBuildingAt(tile);

            if (building is Stable stable)
            {
                this.ShowHorseSwapMenu(stable);

            }
        }

        private void ShowHorseSwapMenu(Stable targetStable)
        {
            var horses = HorseHelper.GetAllBarnHorses().
            Where(h => !HorseHelper.IsHidden(h))
            .ToList();

            Game1.activeClickableMenu = new HorseSwapMenu(horses, (selectedHorse) =>
            {
                this.Monitor.Log($"Swapping to: {selectedHorse.Name}", LogLevel.Info);

                HorseHelper.SwapStableHorse(
                    selectedHorse,
                    targetStable,
                    this.Monitor,
                    this.Helper,
                    this.SetHorseSkin
                );

                Game1.exitActiveMenu();
            });
        }

        private void OnRenderedActiveMenu(object sender, RenderedActiveMenuEventArgs e)
        {
            // 1. Only draw if the AnimalQueryMenu is open
            if (Game1.activeClickableMenu is AnimalQueryMenu menu)
            {
                // 2. Access the static button from your patch class
                var statsButton = MenuPatches.StatsButton;
                if (statsButton == null) return;

                // 3. Hide if menu is busy (Vanilla or AHM)
                if (menu.movingAnimal || menu.confirmingSell) return;

                // 4. Manual Hover Logic
                int mouseX = Game1.getOldMouseX();
                int mouseY = Game1.getOldMouseY();

                if (statsButton.containsPoint(mouseX, mouseY))
                {
                    statsButton.scale = Math.Min(statsButton.scale + 0.05f, statsButton.baseScale + 0.5f);
                    menu.hoverText = statsButton.hoverText;
                }
                else
                {
                    statsButton.scale = Math.Max(statsButton.scale - 0.05f, statsButton.baseScale);
                }

                // 5. Draw!
                statsButton.draw(e.SpriteBatch);

                // 6. Draw the mouse cursor on top of the button
                menu.drawMouse(e.SpriteBatch);
            }
        }


        private void processHorseSprint(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.player.mount == null) return;

            // 1. Check for Sprint Key 'R'
            if (e.Button != SButton.R) return;

            // 2. Get Horse Data
            var horse = HorseHelper.GetFarmAnimalForHorse(Game1.player.mount);
            if (horse == null) return;

            var stats = horse.GetHorseStats();

            // 3. Check if already sprinting or in cooldown
            if (Game1.player.buffs.IsApplied("Froshty.HorseTycoon.Sprint") ||
                Game1.player.buffs.IsApplied("Froshty.HorseTycoon.Exhausted"))
            {
                return;
            }

            // 4. Calculate Duration (Total Stamina / 4) in milliseconds
            int durationMs = Math.Clamp((stats.TotalStamina / 4) * 1000, 500, 100000);

            // 5. Apply Sprint Buff
            Buff sprintBuff = new Buff(
                id: "Froshty.HorseTycoon.Sprint",
                displayName: "Horse Sprint",
                duration: durationMs,
                effects: new BuffEffects { Speed = { 1f } }
            );
            Game1.player.applyBuff(sprintBuff);

            // Play a sound to indicate the sprint started
            Game1.playSound("fireball");
        }

        private bool WasSprintingLastCheck = false;

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // 1. Basic safety checks (Runs every tick)
            if (!Context.IsWorldReady || Game1.player.mount == null)
            {
                WasSprintingLastCheck = false;
                return;
            }

            // 2. Check current sprint status
            bool isCurrentlySprinting = Game1.player.buffs.IsApplied("Froshty.HorseTycoon.Sprint");

            // 3. Logic: If we WERE sprinting last frame, but we ARE NOT now...
            if (WasSprintingLastCheck && !isCurrentlySprinting)
            {
                this.Monitor.Log("Sprint ended. Applying exhaustion debuff.", LogLevel.Debug);
                ApplyExhaustion();
            }

            // 4. Update the state for the next tick
            WasSprintingLastCheck = isCurrentlySprinting;
        }

        private void ApplyExhaustion()
        {
            var horse = HorseHelper.GetFarmAnimalForHorse(Game1.player.mount);
            if (horse == null) return;

            // Currently we do a flat 10 sec exhaustion
            int durationMs = 10000;

            Buff tiredBuff = new Buff(
                id: "Froshty.HorseTycoon.Exhausted",
                displayName: "Horse Exhausted",
                duration: durationMs,
                isDebuff: true
            );

            tiredBuff.iconTexture = Game1.buffsIcons;
            tiredBuff.iconSheetIndex = 25;   // Using index 25 (the red 'sick' debuff

            tiredBuff.description = "Your horse needs a break before another sprint!";

            Game1.player.applyBuff(tiredBuff);
        }
    }
}