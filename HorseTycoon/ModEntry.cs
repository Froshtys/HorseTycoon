using HarmonyLib;
using HorseOverhaul.HorseTycoon;
using HorseTycoon.Models;
using HorseTycoon.Patches;
using Microsoft.Xna.Framework;
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
        private JumpManager? jumpManager;
        public override void Entry(IModHelper helper)
        {
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            helper.Events.Display.MenuChanged += this.OnMenuChanged;

            helper.ConsoleCommands.Add("set_horse_stat",
            "Sets a horse's stat.\n\nUsage: set_horse_stat <stat_name> <iv/ev> <value>\n- Example: set_horse_stat Jump EV 50",
            this.HandleSetStat);

            var harmony = new Harmony(this.ModManifest.UniqueID);
            FarmAnimalPatches.Apply(harmony);

            MenuPatches.Initialize(helper, this.Monitor);
            MenuPatches.Apply(harmony);

            ThinHorseDrawPatches.ApplyPatches(harmony);
            ThinHorsePatches.ApplyPatches(harmony);

            HorseTexturePatches.Initialize(helper, this.Monitor);
            HorseTexturePatches.Apply(harmony);

            // Create and start the jump logic
            this.jumpManager = new JumpManager(helper, this.Monitor, this.ModManifest);
            this.jumpManager.Initialize();
        }

        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            if (e.OldMenu is PurchaseAnimalsMenu)
            {
                ConvertUnassignedStableHorses();
            }
            else if (e.OldMenu is NamingMenu)
            {
                ConvertUnassignedStableHorses();
            }
        }
        private void ConvertUnassignedStableHorses()
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

            if (!Context.IsMainPlayer) return;

            foreach (Stable stable in Game1.getFarm().buildings.OfType<Stable>())
            {

                if (stable.isUnderConstruction() || stable.modData.ContainsKey(HorseHelper.CurrentFarmHorseIdKey))
                    continue;

                // Check if the stable is intentionally empty
                bool isEmpty = stable.modData.TryGetValue(HorseHelper.StableEmptyKey, out string isEmptyStr) && isEmptyStr == "true";
                if (isEmpty)
                {
                    Horse overnightClone = stable.getStableHorse();
                    if (overnightClone != null)
                    {
                        Game1.getFarm().characters.Remove(overnightClone);
                        if (overnightClone.currentLocation != null)
                        {
                            overnightClone.currentLocation.characters.Remove(overnightClone);
                        }
                        this.Monitor.Log($"Clear overnight clone horse", LogLevel.Debug);
                    }
                    stable.HorseId = Guid.Empty;
                    continue;
                }

                Building? barn = HorseHelper.GetAvailableBarn();
                if (barn == null)
                {
                    this.Monitor.Log($"Stable horse found, but no barn exists. It will be converted once a barn is built.", LogLevel.Info);
                    continue;
                }

                // Convert a horse from a new stable
                if (stable.daysOfConstructionLeft.Value == 0)
                {
                    Horse horse = stable.getStableHorse();
                    if (horse == null) continue;

                    // Initialize immediately with a default name so farmhands can ride the horse
                    // before the main player finishes naming it
                    string[] defaultNames = { "Ginger", "Thunder", "Lightning", "Blizzard", "Maple", "Amber", "Chocolate", "Applesauce", "Dancer", "Snowy" };
                    string defaultName = defaultNames[Game1.random.Next(defaultNames.Length)];
                    horse.Name = defaultName;
                    horse.displayName = defaultName;
                    HorseHelper.ConvertStableHorseToFarmAnimal(stable, horse, barn, this.Monitor, this.Helper);

                    Game1.showGlobalMessage($"Your new Stable is ready.");
                    Game1.activeClickableMenu = new NamingMenu(
                        processedName =>
                        {
                            horse.Name = processedName;
                            horse.displayName = processedName;

                            FarmAnimal? farmAnimal = HorseHelper.GetFarmAnimalForHorse(horse);
                            if (farmAnimal != null)
                            {
                                farmAnimal.Name = processedName;
                                farmAnimal.displayName = processedName;
                            }

                            this.Monitor.Log($"Named new horse '{processedName}'", LogLevel.Info);
                            Game1.exitActiveMenu();
                        },
                        defaultName: defaultName,
                        title: "Name your new horse:"
                    );
                }
                else
                {
                    //Convert an existing stable horse
                    Horse horse = stable.getStableHorse();
                    if (horse == null) continue;
                    HorseHelper.ConvertStableHorseToFarmAnimal(stable, horse, barn, this.Monitor, this.Helper);
                    this.Monitor.Log($"Successfully converted old horse '{horse.Name}' and converted it into a barn animal", LogLevel.Info);

                }
            }
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (Context.IsMainPlayer)
            {
                ConvertUnassignedStableHorses();
            }
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            HorseTexturePatches.PreloadTextures();
            HorseHelper.MigrateAtSkinKeys(this.Monitor);
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

            // If any menu is currently active, ignore the click entirely for world interactions
            if (Game1.activeClickableMenu != null) return;

            processHorseSprint(sender, e);

            // Only trigger on Left-Click
            if (!e.Button.IsUseToolButton()) return;

            // Don't open the menu if they are trying to fill the stable with water for horse overhaul mod
            if (Game1.player.CurrentItem is StardewValley.Tools.WateringCan)
                return;

            Vector2 clickedTile = e.Cursor.Tile;
            Rectangle mouseRect = new Rectangle((int)e.Cursor.AbsolutePixels.X, (int)e.Cursor.AbsolutePixels.Y, 64, 64);
            Horse? clickedHorse = Game1.currentLocation.characters.OfType<Horse>()
                .FirstOrDefault(h => h.GetBoundingBox().Intersects(mouseRect));

            if (clickedHorse != null)
            {
                this.OpenStatMenuForHorse(clickedHorse);
                return;
            }

            Vector2 tile = e.Cursor.GrabTile;
            Building building = Game1.currentLocation.getBuildingAt(tile);

            if (building is Stable stable)
            {
                this.ShowHorseSwapMenu(stable);
                // Dismount if the player is mounted
                if (Game1.player.mount != null)
                {
                    Game1.player.mount.dismount();
                }
                return;
            }
        }

        private void OpenStatMenuForHorse(Horse horse)
        {
            if (horse == null) return;

            FarmAnimal? animalData = HorseHelper.GetFarmAnimalForHorse(horse);

            if (animalData != null)
            {
                Game1.activeClickableMenu = new AnimalQueryMenu(animalData);
            }
            else
            {
                this.Monitor.Log($"Could not find FarmAnimal data for horse: {horse.name}", LogLevel.Warn);
            }
        }

        private static string? GetHorseReturnBlockReason(Stable stable)
        {
            Guid horseId = stable.HorseId;
            if (horseId == Guid.Empty) return null;

            if (Game1.getOnlineFarmers().Any(f => f.mount != null && f.mount.HorseId == horseId))
                return "Horse is being ridden and can't be put away.";

            bool isOnFarm = Game1.getFarm().characters.OfType<Horse>().Any(h => h.HorseId == horseId);
            if (!isOnFarm)
                return "Horse must be on the farm to be put away.";

            return null;
        }

        private void ShowHorseSwapMenu(Stable targetStable)
        {
            var horses = HorseHelper.GetAllBarnHorses().ToList();

            if (Game1.player.isRidingHorse())
                Game1.player.mount.dismount();

            string? openBlockReason = GetHorseReturnBlockReason(targetStable);
            if (openBlockReason != null)
            {
                Game1.showRedMessage(openBlockReason);
                return;
            }

            // Trace if the targeted stable structures hold a valid custom connection node
            FarmAnimal? activeHorseData = null;
            if (targetStable.modData.TryGetValue(HorseHelper.CurrentFarmHorseIdKey, out string farmIdStr) && long.TryParse(farmIdStr, out long farmId))
            {
                activeHorseData = HorseHelper.GetHiddenHorseById(farmId);
            }

            if (horses.Count == 0 && activeHorseData == null) return;

            // Pins the currently active stable horse to index 0, sorting others alphabetically underneath
            if (activeHorseData != null)
            {
                horses = horses.Where(h => !HorseHelper.IsHidden(h) || h.myID.Value == activeHorseData.myID.Value).ToList();
                horses = horses
                    .OrderByDescending(h => h.myID.Value == activeHorseData.myID.Value)
                    .ThenBy(h => h.Name)
                    .ToList();
            }
            else
            {
                horses = horses.Where(h => !HorseHelper.IsHidden(h)).ToList();
                horses = horses.OrderBy(h => h.Name).ToList();
            }
            // -------------------------

            Game1.activeClickableMenu = new HorseSwapMenu(horses, targetStable, activeHorseData, Helper, (selectedHorse) =>
            {
                string? returnBlockReason = GetHorseReturnBlockReason(targetStable);
                if (returnBlockReason != null)
                {
                    Game1.showRedMessage(returnBlockReason);
                    return;
                }

                // --- CASE: Player clicked "Return to Barn" (Or selected the already active horse row) ---
                if (selectedHorse == null || (activeHorseData != null && selectedHorse.myID.Value == activeHorseData.myID.Value))
                {
                    Horse physicalHorse = targetStable.getStableHorse();

                    this.Monitor.Log("Returning active mount to barn and leaving stable empty.", LogLevel.Info);

                    // Unhide data and strip tracking parameters
                    if (activeHorseData != null)
                    {
                        HorseHelper.RestoreHorse(activeHorseData);
                    }
                    if (physicalHorse != null)
                    {
                        Game1.getFarm().characters.Remove(physicalHorse);
                        physicalHorse.currentLocation?.characters.Remove(physicalHorse);
                    }

                    targetStable.modData.Remove(HorseHelper.CurrentFarmHorseIdKey);
                    targetStable.modData[HorseHelper.StableEmptyKey] = "true";
                    targetStable.HorseId = Guid.Empty;
                }
                // --- CASE: Player selected a new horse from list ---
                else
                {
                    this.Monitor.Log($"Swapping to: {selectedHorse.Name}", LogLevel.Info);
                    targetStable.modData.Remove(HorseHelper.StableEmptyKey);
                    HorseHelper.SwapStableHorse(selectedHorse, targetStable, this.Monitor, this.Helper);
                }

                Game1.exitActiveMenu();
            });

            this.Helper.Input.Suppress(SButton.MouseLeft);
        }
        private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
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

            // 4. Calculate Duration (Total Sprint / 4) in milliseconds
            int durationMs = Math.Clamp((stats.TotalSprint / 4) * 1000, 1000, 100000);

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
            TrainingManager.ProcessSprint(Game1.player.mount);
        }

        private bool WasSprintingLastCheck = false;

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
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

            OnUpdateTickedProcessDistance(sender, e);
        }

        private readonly PerScreen<Vector2> lastPosition = new(() => Vector2.Zero);

        private void OnUpdateTickedProcessDistance(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.player.mount == null)
            {
                lastPosition.Value = Vector2.Zero;
                return;
            }

            // Calculate distance moved since last tick
            if (lastPosition.Value != Vector2.Zero)
            {
                float distance = Vector2.Distance(lastPosition.Value, Game1.player.Position);

                // Ignore teleporting (loading screens)
                if (distance > 0 && distance < 100)
                {
                    TrainingManager.ProcessMovement(Game1.player.mount, distance);
                }
            }

            lastPosition.Value = Game1.player.Position;
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
            )
            {
                iconTexture = Game1.buffsIcons,
                iconSheetIndex = 25,   // Index 25 (the red 'sick' debuff)
                description = "Your horse needs a break before another sprint!"
            };
            Game1.player.applyBuff(tiredBuff);
        }

        private void HandleSetStat(string command, string[] args)
        {
            if (!Context.IsWorldReady || Game1.player.mount == null)
            {
                this.Monitor.Log("You must be riding a horse!", LogLevel.Error);
                return;
            }

            if (args.Length < 3)
            {
                this.Monitor.Log("Usage: set_horse_stat <Stat> <IV/EV> <Value>", LogLevel.Error);
                return;
            }

            var horse = HorseHelper.GetFarmAnimalForHorse(Game1.player.mount);
            if (horse == null) return;

            var stats = horse.GetHorseStats();

            // Attempt to apply the stat via our new class method
            if (int.TryParse(args[2], out int val) && stats.ApplyDebugStat(args[0], args[1], val))
            {
                this.Monitor.Log($"Updated {horse.Name}: {args[0]} {args[1]} set to {val}.", LogLevel.Info);
            }
            else
            {
                this.Monitor.Log("Invalid stat name or type. Use: Jump/Speed/Sprint and IV/EV.", LogLevel.Error);
            }
        }
    }
}