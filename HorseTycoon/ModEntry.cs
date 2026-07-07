using HarmonyLib;
using HorseOverhaul.HorseTycoon;
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
        private JumpManager? jumpManager;
        private FestivalRaceManager? festivalRaceManager;
        private Texture2D? sprintBuffIcon;

        public override void Entry(IModHelper helper)
        {
            Logger.Init(this.Monitor);
            sprintBuffIcon = helper.ModContent.Load<Texture2D>("assets/HorseRunningBuff.png");
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.Content.AssetRequested += this.OnAssetRequested;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            helper.Events.Display.MenuChanged += this.OnMenuChanged;
            helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;

            helper.ConsoleCommands.Add("set_horse_stat",
            "Sets a horse's stat.\n\nUsage: set_horse_stat <stat_name> <iv/ev> <value>\n- Example: set_horse_stat Jump EV 50",
            this.HandleSetStat);

            helper.ConsoleCommands.Add("horse_pregnancy",
            "Starts or fast-forwards a horse pregnancy for testing.\n\nUsage: horse_pregnancy <horse name> [daysLeft]\n- Omit daysLeft to start a fresh 7-day pregnancy.\n- Example: horse_pregnancy Thunder 1 (gives birth tomorrow)",
            this.HandlePregnancyCommand);

            helper.ConsoleCommands.Add("give_gold_carrot",
            "Adds Gold Carrots to your inventory for testing the breeding pen.\n\nUsage: give_gold_carrot [count]",
            (cmd, args) =>
            {
                if (!Context.IsWorldReady) { this.Monitor.Log("Load a save first.", LogLevel.Warn); return; }
                int count = args.Length > 0 && int.TryParse(args[0], out int c) ? c : 5;
                Item carrot = ItemRegistry.Create($"(O){BreedingPenManager.GoldCarrotItemId}", count);
                Game1.player.addItemByMenuIfNecessary(carrot);
                this.Monitor.Log($"Gave {count} Gold Carrot(s).", LogLevel.Info);
            });

            var harmony = new Harmony(this.ModManifest.UniqueID);
            FarmAnimalPatches.Apply(harmony);
            PregnancyPatches.Apply(harmony);

            MenuPatches.Initialize(helper, this.Monitor);
            MenuPatches.Apply(harmony);

            ThinHorseDrawPatches.ApplyPatches(harmony);
            ThinHorsePatches.ApplyPatches(harmony);

            HorseTexturePatches.Initialize(helper, this.Monitor);
            HorseTexturePatches.Apply(harmony);

            // Horse-face markers on the Billboard calendar for all three horse festivals
            CalendarPatches.Apply(harmony);

            // Create and start the jump logic
            this.jumpManager = new JumpManager(helper, this.Monitor, this.ModManifest);
            this.jumpManager.Initialize();

            // Spring 21 Horse Festival race logic
            this.festivalRaceManager = new FestivalRaceManager(helper, this.Monitor);
            this.festivalRaceManager.Initialize();

            // Robin-built bus horse trailer (required for away festivals)
            BusTrailerManager.Initialize(helper);

            // Festival horse market (Horse Seller + Stud Shop NPCs)
            HorseMarket.Initialize(helper, this.Monitor);

            // Robin-built horse breeding pen
            BreedingPenManager.Initialize(helper, this.Monitor);
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (e.OldMenu is PurchaseAnimalsMenu or NamingMenu)
                ConvertUnassignedStableHorses();
        }
        private void ConvertUnassignedStableHorses()
        {
            this.InitializeNewHorseStats();

            // Stable conversion mutates host-owned building/animal data — host only.
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

                // Convert the stable horse (the isUnderConstruction() guard above means the stable is finished).
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
        }

        // Farmhand -> host: roll Starter stats for a freshly purchased horse. The host is the only
        // writer (so two clients can never race to roll different IVs), but the farmhand triggers it
        // immediately on buying so the stats appear as soon as the modData syncs back.
        private const string MsgInitHorseStats = "HorseTycoon.InitHorseStats";
        private record InitHorseStatsMessage(long HorseId);

        /// <summary>Rolls Starter stats for any horse that has none yet. The host rolls directly;
        /// farmhands ask the host via <see cref="MsgInitHorseStats"/> (idempotent on the host, so
        /// duplicate requests are harmless).</summary>
        private void InitializeNewHorseStats()
        {
            Utility.ForEachLocation(location =>
            {
                foreach (FarmAnimal animal in location.animals.Values)
                {
                    // Only horses that are "new" (no SpeedIV set yet).
                    if (!animal.type.Value.Contains("Horse") || animal.modData.ContainsKey(HorseStats.SpeedIVKey))
                        continue;

                    if (Context.IsMainPlayer)
                    {
                        this.Monitor.Log($"Initializing stats for new horse: {animal.Name}", LogLevel.Debug);
                        animal.GetHorseStats().RandomizeStats(HorseSourceQuality.Starter);
                        HorseHelper.LogHorseData(animal, this.Monitor);
                    }
                    else
                    {
                        this.Helper.Multiplayer.SendMessage(
                            new InitHorseStatsMessage(animal.myID.Value),
                            MsgInitHorseStats,
                            modIDs: new[] { this.Helper.ModRegistry.ModID });
                    }
                }
                return true; // Continue to next location
            });
        }

        private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != this.Helper.ModRegistry.ModID || e.Type != MsgInitHorseStats || !Context.IsMainPlayer)
                return;

            long horseId = e.ReadAs<InitHorseStatsMessage>().HorseId;
            FarmAnimal? horse = HorseHelper.GetAllBarnHorses().FirstOrDefault(a => a.myID.Value == horseId);
            if (horse == null || horse.modData.ContainsKey(HorseStats.SpeedIVKey))
                return;

            this.Monitor.Log($"Initializing stats for farmhand-purchased horse: {horse.Name}", LogLevel.Debug);
            horse.GetHorseStats().RandomizeStats(HorseSourceQuality.Starter);
            HorseHelper.LogHorseData(horse, this.Monitor);
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (Context.IsMainPlayer)
            {
                ConvertUnassignedStableHorses();
                TrainingManager.ResetDailyCounters();
                BreedingManager.OnDayStarted();
                BreedingPenManager.OnDayStarted();
            }
        }

        private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Quests"))
            {
                e.Edit(asset =>
                {
                    asset.AsDictionary<string, string>().Data[FestivalRaceManager.BetRewardQuestId] =
                        "Basic/Horse Race Bet/You called it right. Here's what you're owed. Don't spend it all in one place. - Pam/Collect your winnings.";
                    asset.AsDictionary<string, string>().Data[FestivalRaceManager.BetRewardAwayQuestId] =
                        "Basic/Horse Race Bet/Good call. Here's your payout, counted twice. Pleasure doing business. - The Bouncer/Collect your winnings.";
                });
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

            // Breeding pen: right-click assigns horses, or feeds a held Gold Carrot.
            if (e.Button.IsActionButton())
            {
                Building? penBuilding = Game1.currentLocation.getBuildingAt(e.Cursor.GrabTile);
                if (BreedingPenManager.IsBreedingPen(penBuilding))
                {
                    BreedingPenManager.HandleActionClick(penBuilding!, Game1.player);
                    this.Helper.Input.Suppress(e.Button);
                    return;
                }
            }

            // Only trigger on Left-Click
            if (!e.Button.IsUseToolButton()) return;

            // Don't open the menu if they are trying to fill the stable with water for horse overhaul mod
            if (Game1.player.CurrentItem is StardewValley.Tools.WateringCan)
                return;

            Vector2 clickedTile = e.Cursor.Tile;
            Rectangle mouseRect = new Rectangle((int)e.Cursor.AbsolutePixels.X, (int)e.Cursor.AbsolutePixels.Y, 64, 64);
            Horse? clickedHorse = Game1.currentLocation.characters.OfType<Horse>()
                .FirstOrDefault(h => !h.modData.ContainsKey(HorseHelper.NoTackKey) && h.GetBoundingBox().Intersects(mouseRect));

            if (clickedHorse != null)
            {
                this.OpenStatMenuForHorse(clickedHorse);
                // Suppress so the same click doesn't fall through to the new menu's buttons
                this.Helper.Input.Suppress(e.Button);
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
                this.Helper.Input.Suppress(e.Button);
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
                this.Monitor.Log($"Could not find FarmAnimal data for horse: {horse.Name}", LogLevel.Warn);
            }
        }

        private static string? GetHorseReturnBlockReason(Stable stable)
        {
            Guid horseId = stable.HorseId;
            if (horseId == Guid.Empty) return null;

            if (Game1.getOnlineFarmers().Any(f => f.mount != null && f.mount.HorseId == horseId))
                return "Horse is being ridden.";

            bool isOnFarm = Game1.getFarm().characters.OfType<Horse>().Any(h => h.HorseId == horseId);
            if (!isOnFarm)
                return "Horse is not on the farm.";

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
                // 2. Hide if menu is busy (Vanilla or AHM)
                if (menu.movingAnimal || menu.confirmingSell) return;

                // 3. Gender marker to the right of the name box (horses only) — vanilla
                // male/female symbols from the character-creation menu.
                if (menu.animal != null && menu.animal.type.Value.Contains("Horse"))
                {
                    Rectangle genderSource = menu.animal.isMale()
                        ? new Rectangle(128, 192, 16, 16)
                        : new Rectangle(144, 192, 16, 16);
                    Vector2 genderPos = new Vector2(menu.textBox.X + menu.textBox.Width + 12, menu.textBox.Y + 12);
                    e.SpriteBatch.Draw(Game1.mouseCursors, genderPos, genderSource, Color.White, 0f, Vector2.Zero, 2.5f, SpriteEffects.None, 0.9f);
                }

                // 4. Access the static button from your patch class
                var statsButton = MenuPatches.StatsButton;
                if (statsButton == null)
                {
                    menu.drawMouse(e.SpriteBatch);
                    return;
                }

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

            // During the Horse Festival race, FestivalRaceManager runs a custom sprint instead (the vanilla
            // buff timer is frozen while the festival pauses time).
            if (FestivalRaceManager.RaceRidingActive) return;

            // 1. Check for Sprint Key (Left or Right Shift)
            if (e.Button != SButton.LeftShift && e.Button != SButton.RightShift) return;

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

            // 4. Shared HorseStats formula so this buff matches the festival sprint.
            int durationMs = HorseStats.SprintDurationMs(stats.TotalSprint);
            float speedBonus = HorseStats.SprintSpeedBonus(stats.TotalSprint);
            Logger.LogVerbose($"Sprint (world): sprint={stats.TotalSprint}, duration={durationMs}ms, speed=+{speedBonus}");

            // 5. Apply Sprint Buff
            Buff sprintBuff = new Buff(
                id: "Froshty.HorseTycoon.Sprint",
                displayName: "Horse Sprint",
                duration: durationMs,
                effects: new BuffEffects { Speed = { speedBonus } }
            )
            {
                iconTexture = sprintBuffIcon,
                iconSheetIndex = 0,
            };
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
                // If the player dismounted while the sprint buff was active, swap it for exhaustion.
                if (WasSprintingLastCheck || Game1.player.buffs.IsApplied("Froshty.HorseTycoon.Sprint"))
                {
                    Game1.player.buffs.Remove("Froshty.HorseTycoon.Sprint");
                    ApplyExhaustion();
                }
                WasSprintingLastCheck = false;
                // Clear the distance anchor while unmounted so the first tick after (re)mounting — possibly
                // in a new location or the next morning — doesn't credit the gap since the last dismount.
                lastPosition.Value = Vector2.Zero;
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

        /// <summary>Only called from <see cref="OnUpdateTicked"/> while the player is mounted (the
        /// unmounted path clears <see cref="lastPosition"/> and returns before reaching this).</summary>
        private void OnUpdateTickedProcessDistance(object? sender, UpdateTickedEventArgs e)
        {
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

        private void HandlePregnancyCommand(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("Load a save first.", LogLevel.Error);
                return;
            }

            if (args.Length < 1)
            {
                this.Monitor.Log("Usage: horse_pregnancy <horse name> [daysLeft]", LogLevel.Error);
                return;
            }

            FarmAnimal? mare = HorseHelper.GetAllBarnHorses()
                .FirstOrDefault(h => h.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
            if (mare == null)
            {
                this.Monitor.Log($"No barn horse named '{args[0]}' found.", LogLevel.Error);
                return;
            }

            if (args.Length >= 2 && int.TryParse(args[1], out int daysLeft))
            {
                mare.modData[HorseHelper.PregnancyDaysLeftKey] = Math.Max(1, daysLeft).ToString();
                BreedingManager.SendToBirthingArea(mare);
                this.Monitor.Log($"{mare.Name} is now pregnant with {Math.Max(1, daysLeft)} day(s) left.", LogLevel.Info);
            }
            else
            {
                BreedingManager.MakePregnant(mare);
                this.Monitor.Log($"{mare.Name} is now pregnant ({BreedingManager.GestationDays} days).", LogLevel.Info);
            }
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