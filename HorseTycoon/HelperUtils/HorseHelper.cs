using HorseTycoon.Models;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;

namespace HorseTycoon
{
    public static class HorseHelper
    {
        public const string CurrentFarmHorseIdKey = "Froshty.HorseTycoon/CurrentFarmHorseId";
        public const string HideKey = "Froshty.HorseTycoon/IsHidden";
        public const string StableEmptyKey = "Froshty.HorseTycoon/IsIntentionallyEmpty";
        public const string HorseSkinKey = "Froshty.HorseTycoon/HorseSkin";
        // Comma-separated overlay names. Absent or empty = no overlays.
        public const string OverlaysKey = "Froshty.HorseTycoon/Overlays";
        // Marks a proxy Horse that should render with NO saddle/bridle (e.g. breeding-pen horses).
        // Without this, an empty OverlaysKey defaults to brown tack (see HorseTexturePatches).
        public const string NoTackKey = "Froshty.HorseTycoon/NoTack";
        // Unqualified item ID of the currently equipped saddle (e.g. "HorseTycoon.SaddleBrown").
        public const string EquippedSaddleKey = "Froshty.HorseTycoon/EquippedSaddle";
        // Days remaining until a pregnant mare gives birth. Absent = not pregnant.
        public const string PregnancyDaysLeftKey = "Froshty.HorseTycoon/PregnancyDaysLeft";
        // "speed,sprint,jump" IVs of the stud that sired the current pregnancy (festival stud shop).
        // Absent = no known sire; the foal rolls random Starter stats instead of inheriting.
        public const string SireIVsKey = "Froshty.HorseTycoon/PregnancySireIVs";
        // Fallback stat keys written directly onto a borrowed festival horse (no FarmAnimal backing).
        public const string BorrowedSpeedKey = "Froshty.HorseTycoon/BorrowedSpeed";
        public const string BorrowedSprintKey = "Froshty.HorseTycoon/BorrowedSprint";
        public const string BorrowedJumpKey = "Froshty.HorseTycoon/BorrowedJump";

        // Maps unqualified saddle item ID → "Saddle_X,Bridle_X" overlay string.
        public static readonly IReadOnlyDictionary<string, string> SaddleItemOverlays =
            new Dictionary<string, string>
            {
                ["HorseTycoon.SaddleBrown"]  = "Saddle_Brown,Bridle_Brown",
                ["HorseTycoon.SaddleWhite"]  = "Saddle_White,Bridle_White",
                ["HorseTycoon.SaddleBlack"]  = "Saddle_Black,Bridle_Black",
                ["HorseTycoon.SaddleRed"]    = "Saddle_Red,Bridle_Red",
                ["HorseTycoon.SaddleOrange"] = "Saddle_Orange,Bridle_Orange",
                ["HorseTycoon.SaddleTeal"]   = "Saddle_Teal,Bridle_Teal",
                ["HorseTycoon.SaddleIce"]      = "Saddle_Ice,Bridle_Ice",
                ["HorseTycoon.SaddleLavender"] = "Saddle_Lavender,Bridle_Lavender",
                ["HorseTycoon.SaddleRainbow"]  = "Saddle_Rainbow,Bridle_Rainbow",
                ["HorseTycoon.SaddleTrans"]    = "Saddle_Trans,Bridle_Trans",
                ["HorseTycoon.SaddleLesbian"]  = "Saddle_Lesbian,Bridle_Lesbian",
                ["HorseTycoon.SaddleAce"]       = "Saddle_Ace,Bridle_Ace",
                ["HorseTycoon.SaddleNonBinary"] = "Saddle_NonBinary,Bridle_NonBinary",
                ["HorseTycoon.SaddleBisexual"]  = "Saddle_Bisexual,Bridle_Bisexual",
            };

        public static bool IsSaddleItem(Item? item) =>
            item != null && SaddleItemOverlays.ContainsKey(item.ItemId);

        public static string GetEquippedSaddleId(Horse horse) =>
            horse.modData.TryGetValue(EquippedSaddleKey, out string? id) ? id : "HorseTycoon.SaddleBrown";

        public static void EquipSaddle(Horse horse, string itemId)
        {
            horse.modData[EquippedSaddleKey] = itemId;
            if (SaddleItemOverlays.TryGetValue(itemId, out string? overlays))
                horse.modData[OverlaysKey] = overlays;
        }
        public static string? GetOverlaysRaw(FarmAnimal animal) =>
            animal.modData.TryGetValue(OverlaysKey, out string? v) ? v : null;

        public static void SetOverlays(FarmAnimal animal, IEnumerable<string> overlayNames)
        {
            animal.modData[OverlaysKey] = string.Join(",", overlayNames);

            // If this animal is currently active in a stable, sync to the Horse character too.
            Utility.ForEachCharacter(c =>
            {
                if (c is Horse horse)
                {
                    Stable? stable = Game1.getFarm().buildings.OfType<Stable>()
                        .FirstOrDefault(s => s.HorseId == horse.HorseId);
                    if (stable != null &&
                        stable.modData.TryGetValue(CurrentFarmHorseIdKey, out string? idStr) &&
                        idStr == animal.myID.Value.ToString())
                    {
                        horse.modData[OverlaysKey] = animal.modData[OverlaysKey];
                        return false;
                    }
                }
                return true;
            });
        }

        public static List<FarmAnimal> GetAllBarnHorses()
        {
            List<FarmAnimal> horsesFound = new();
            Farm farm = Game1.getFarm();

            foreach (FarmAnimal animal in farm.animals.Values)
            {
                if (animal.type.Value.Contains("Horse"))
                    horsesFound.Add(animal);
            }

            foreach (Building building in farm.buildings)
            {
                if (building.indoors.Value is AnimalHouse barnInterior)
                {
                    foreach (FarmAnimal animal in barnInterior.animals.Values)
                    {
                        if (animal.type.Value.Contains("Horse") && !horsesFound.Contains(animal))
                        {
                            horsesFound.Add(animal);
                        }
                    }
                }
            }
            return horsesFound;
        }

        public static FarmAnimal? GetHiddenHorseById(long targetId)
        {
            foreach (FarmAnimal horse in GetAllBarnHorses())
            {
                if (horse.myID.Get() == targetId &&
                    horse.modData != null &&
                    horse.modData.TryGetValue(HideKey, out string isHidden) &&
                    isHidden == "true")
                {
                    return horse;
                }
            }
            return null;
        }

        /// <summary>Finds the FarmAnimal data associated with a specific mountable Horse.</summary>
        /// <param name="mount">The horse character the player is riding.</param>
        public static FarmAnimal? GetFarmAnimalForHorse(Horse mount)
        {
            if (mount == null) return null;

            // 1. Get the stable associated with this horse
            Stable? stable = Game1.getFarm().buildings.OfType<Stable>().FirstOrDefault(s => s.HorseId == mount.HorseId);

            if (stable != null && stable.modData.TryGetValue(CurrentFarmHorseIdKey, out string idStr))
            {
                if (long.TryParse(idStr, out long farmAnimalId))
                {
                    // 2. Use our existing lookup logic to find the FarmAnimal by its ID
                    return GetHiddenHorseById(farmAnimalId);
                }
            }

            return null;
        }

        /// <summary>
        /// Returns (SpeedBoost, TotalSprint) for a mounted horse.
        /// Uses FarmAnimal stats when available; falls back to borrowed-stat modData keys for
        /// temporary festival horses that have no FarmAnimal backing.
        /// </summary>
        public static (float SpeedBoost, int TotalSprint) GetRaceStats(Horse mount)
        {
            var animal = GetFarmAnimalForHorse(mount);
            if (animal != null)
            {
                var stats = animal.GetHorseStats();
                return (stats.SpeedBoost, stats.TotalSprint);
            }
            int speed  = mount.modData.TryGetValue(BorrowedSpeedKey,  out string sv) && int.TryParse(sv,  out int s) ? s : 0;
            int sprint = mount.modData.TryGetValue(BorrowedSprintKey, out string pv) && int.TryParse(pv, out int p) ? p : 0;
            return (speed / 40f, sprint);
        }

        // The "this" keyword is what makes it an extension method!
        public static HorseStats GetHorseStats(this FarmAnimal animal)
        {
            return new HorseStats(animal);
        }

        /// <summary>Logs all modData keys and values for a specific horse to the console.</summary>
        /// <param name="animal">The FarmAnimal to inspect.</param>
        /// <param name="monitor">The SMAPI Monitor from your ModEntry.</param>
        public static void LogHorseData(FarmAnimal animal, IMonitor monitor)
        {
            if (animal == null)
            {
                monitor.Log("Cannot log data: Horse animal is null.", LogLevel.Warn);
                return;
            }

            monitor.Log($"--- ModData Report for {animal.Name} (ID: {animal.myID}) ---", LogLevel.Info);

            foreach (string key in animal.modData.Keys)
            {
                monitor.Log($"  [Key]: {key,-40} | [Value]: {animal.modData[key]}", LogLevel.Info);
            }

            monitor.Log("---------------------------------------------------------", LogLevel.Info);
        }

        /// <summary>True if this mountable Horse is a stable horse managed by this mod (i.e. it has a
        /// backing FarmAnimal record). Tractors and other unmanaged horses return false.</summary>
        public static bool IsManagedStableHorse(Horse horse)
        {
            if (horse == null || horse.IsTractor()) return false;

            Stable? stable = Game1.getFarm().buildings.OfType<Stable>()
                .FirstOrDefault(s => s.HorseId == horse.HorseId);

            return stable != null
                && !stable.IsTractorGarage()
                && stable.modData.ContainsKey(CurrentFarmHorseIdKey);
        }

        public static bool IsPregnant(FarmAnimal animal)
        {
            return animal != null && animal.modData.ContainsKey(PregnancyDaysLeftKey);
        }

        public static int GetPregnancyDaysLeft(FarmAnimal animal)
        {
            return animal.modData.TryGetValue(PregnancyDaysLeftKey, out string value) &&
                   int.TryParse(value, out int days)
                ? days
                : 0;
        }

        public static bool IsHidden(FarmAnimal animal)
        {
            return animal != null &&
                   animal.modData.TryGetValue(HideKey, out string value) &&
                   value == "true";
        }

        public static void RestoreHorse(FarmAnimal horse)
        {
            if (horse == null) return;

            horse.modData.Remove(HideKey);
            horse.pauseTimer = 0;

            GameLocation currentLoc = horse.currentLocation ?? Game1.getFarm();

            if (horse.home?.indoors.Value is AnimalHouse homeInterior)
            {
                // Add it to the new (inside) dictionary if not already inside
                if (!homeInterior.animals.ContainsKey(horse.myID.Value))
                {
                    homeInterior.animals.Add(horse.myID.Value, horse);

                    // Remove it from the current (outside) dictionary to prevent duplicates
                    if (currentLoc.animals.ContainsKey(horse.myID.Value))
                    {
                        currentLoc.animals.Remove(horse.myID.Value);
                    }

                    // Find a safe spot inside and put it there
                    Vector2 spawnTile = new Vector2(homeInterior.map.Layers[0].LayerWidth / 2, homeInterior.map.Layers[0].LayerHeight / 2);
                    spawnTile = Utility.recursiveFindOpenTileForCharacter(horse, homeInterior, spawnTile, 10);

                    horse.currentLocation = homeInterior;
                    horse.Position = spawnTile * 64f;
                }
            }
        }

        public static void SwapStableHorse(FarmAnimal selectedBarnHorse, Stable targetStable, IMonitor monitor, IModHelper helper)
        {
            if (selectedBarnHorse == null || targetStable == null)
            {
                monitor.Log("Could not find stable horse or selected barn horse.", LogLevel.Error);
                return;
            }

            Horse? activeHorse = targetStable.getStableHorse();

            // If the horse that was in this stable was sold
            if (activeHorse == null)
            {
                monitor.Log($"Stable has no active horse. Instantiating a new active mount for {selectedBarnHorse.Name}.", LogLevel.Info);

                if (targetStable.modData.ContainsKey(StableEmptyKey))
                {
                    // Remove the lock since a new horse is moving in
                    targetStable.modData.Remove(StableEmptyKey);
                }
                Guid newHorseGuid = Guid.NewGuid();

                // Spawn coordinates matching the stable structure's placement position
                int tileX = targetStable.tileX.Get() + 1;
                int tileY = targetStable.tileY.Get() + 1;

                activeHorse = new Horse(newHorseGuid, tileX, tileY);

                // Link the stable's architectural data profile to the newly spawned character Guid
                targetStable.HorseId = newHorseGuid;

                // Force register the character into the active farm simulation zone
                activeHorse.Name = selectedBarnHorse.Name;
                activeHorse.displayName = selectedBarnHorse.displayName;
                // Force multiplayer update
                activeHorse.Name = selectedBarnHorse.Name;
                Game1.getFarm().characters.Add(activeHorse);
            }
            // Handle Existing Hidden Horse (Only runs if a horse was already assigned)
            else if (targetStable.modData.TryGetValue(CurrentFarmHorseIdKey, out string farmAnimalIdStr))
            {
                if (long.TryParse(farmAnimalIdStr, out long farmAnimalId))
                {
                    FarmAnimal? hiddenHorse = GetHiddenHorseById(farmAnimalId);
                    if (hiddenHorse != null)
                    {
                        RestoreHorse(hiddenHorse);
                    }
                    else
                    {
                        monitor.Log($"Could not find a hidden horse with ID {farmAnimalId} in any game location.", LogLevel.Warn);
                    }
                }
            }

            // Hide and Assign the New Horse
            activeHorse.Name = selectedBarnHorse.Name;
            activeHorse.displayName = selectedBarnHorse.displayName;

            // Track internal placement variables using standard 1.6 properties
            selectedBarnHorse.modData["Froshty.HorseTycoon/CurrentStableId"] = targetStable.id.ToString();
            selectedBarnHorse.modData[HideKey] = "true";
            targetStable.modData[CurrentFarmHorseIdKey] = selectedBarnHorse.myID.Value.ToString();
            targetStable.grabHorse();
            monitor.Log($"Save hidden horse with ID {selectedBarnHorse.myID}.", LogLevel.Debug);

            // 4. Visual Swapping
            string skinId = selectedBarnHorse.skinID.Value ?? "";
            monitor.Log($"Horse skin selected: {skinId}", LogLevel.Info);
            SetHorseSkin(activeHorse, skinId, selectedBarnHorse, monitor);
            monitor.Log($"Successfully swapped active mount to {activeHorse.Name}!", LogLevel.Info);
        }

        public static void ConvertStableHorseToFarmAnimal(Stable stable, Horse horse, Building barn, IMonitor monitor, IModHelper helper)
        {
            // 1. Generate unique ID via Game1
            long newId = Game1.Multiplayer.getNewID();
            FarmAnimal newHorse = new FarmAnimal("Tycoon.Horse", newId, Game1.player.UniqueMultiplayerID);

            newHorse.Name = horse.Name;
            newHorse.modData[HideKey] = "true";

            // Set the new horse age to adult either 28 days or total days player has played.
            int totalDaysPlayed = (int)Game1.stats.DaysPlayed;
            int matureAge = Math.Max(28, totalDaysPlayed);
            newHorse.age.Value = matureAge;

            // 2. Initialize Stats using your extension method
            var stats = newHorse.GetHorseStats();
            stats.RandomizeStats(HorseStats.HorseSourceQuality.Starter);

            // 3. Link to Stable
            stable.modData[CurrentFarmHorseIdKey] = newHorse.myID.Value.ToString();

            // 4. Assign to Barn
            newHorse.homeInterior = barn.GetIndoors() as AnimalHouse;
            newHorse.home = barn;

            // Force sprite to reflect adult age — the constructor loads the baby sprite at age 0,
            // so reload() must be called after age and home are set.
            newHorse.reload(barn);

            // Force into Barn list (bypassing capacity)
            barn.GetIndoors().animals.Add(newHorse.myID.Value, newHorse);

            string skinId = newHorse.skinID.Value ?? "";
            monitor.Log($"Horse skin selected: {skinId}", LogLevel.Info);
            SetHorseSkin(horse, skinId, newHorse, monitor);

            monitor.Log($"Successfully converted stable horse '{horse.Name}' and moved to {barn.buildingType.Value}.", LogLevel.Info);
        }

        public static Building? GetAvailableBarn()
        {
            // 1. Get all buildings that are barns
            var barns = Game1.getFarm().buildings
                .Where(b => b.buildingType.Value.Contains("Barn"))
                .ToList();

            if (!barns.Any()) return null;

            // 2. Find a barn using GetIndoors() instead of animalHouse
            foreach (var b in barns)
            {
                if (b.GetIndoors() is AnimalHouse house)
                {
                    // If there is room, take it. Otherwise, we'll fallback to the first one.
                    if (house.animals.Count() < b.maxOccupants.Value)
                        return b;
                }
            }

            // 3. Fallback: Return the first barn's interior even if full
            return barns.First();
        }

        /// <summary>Horse housing capacity bonus: +1 barn slot per stable owned. A stable-active
        /// horse is hidden but still occupies its home barn's animal list, so each stable
        /// effectively frees up one barn slot. Tractor garages don't count.</summary>
        public static int GetStableCapacityBonus() =>
            Game1.getFarm().buildings.OfType<Stable>().Count(s => !s.IsTractorGarage());

        /// <summary>
        /// Finds a barn with room for another horse, or null if the farm's horse housing is full.
        /// Unlike <see cref="GetAvailableBarn"/> this never falls back to a full barn: a barn with a
        /// vanilla free slot is preferred; otherwise overflow is allowed as long as the total animals
        /// across all barns stay under the combined barn capacity plus the stable bonus (counted once
        /// farm-wide, not per barn).
        /// </summary>
        public static Building? GetBarnWithHorseSpace()
        {
            var barns = Game1.getFarm().buildings
                .Where(b => b.buildingType.Value.Contains("Barn") && b.GetIndoors() is AnimalHouse)
                .ToList();
            if (!barns.Any()) return null;

            foreach (var b in barns)
            {
                if (b.GetIndoors() is AnimalHouse house && house.animals.Count() < b.maxOccupants.Value)
                    return b;
            }

            int totalAnimals = barns.Sum(b => ((AnimalHouse)b.GetIndoors()).animals.Count());
            int totalCapacity = barns.Sum(b => b.maxOccupants.Value) + GetStableCapacityBonus();
            if (totalAnimals < totalCapacity)
            {
                // Least-overfull barn takes the overflow slot.
                return barns.OrderBy(b => ((AnimalHouse)b.GetIndoors()).animals.Count() - b.maxOccupants.Value).First();
            }

            return null;
        }

        /// <summary>
        /// Copies a barn horse's coat onto a proxy <see cref="Horse"/> character and marks it to
        /// render without any saddle/bridle (used by the breeding pen). The proxy is purely visual.
        /// </summary>
        public static void ApplyProxyAppearance(Horse proxy, FarmAnimal source)
        {
            proxy.modData[HorseSkinKey] = SkinIdToName(source.skinID.Value ?? "");
            proxy.modData.Remove(OverlaysKey);
            proxy.modData[NoTackKey] = "true";
        }

        private static void SetHorseSkin(Horse horse, string skinId, FarmAnimal? sourceAnimal, IMonitor monitor)
        {
            horse.modData[HorseSkinKey] = SkinIdToName(skinId);

            // Sync overlay list from the animal if it has one explicitly set.
            if (sourceAnimal != null && sourceAnimal.modData.ContainsKey(OverlaysKey))
            {
                horse.modData[OverlaysKey] = sourceAnimal.modData[OverlaysKey];
            }
            else if (!horse.modData.ContainsKey(EquippedSaddleKey))
            {
                // New horse — default to brown saddle.
                EquipSaddle(horse, "HorseTycoon.SaddleBrown");
            }

            monitor.Log($"Set horse skin to '{horse.modData[HorseSkinKey]}' (from skinId '{skinId}')", LogLevel.Debug);
        }

        private static string SkinIdToName(string skinId) => skinId switch
        {
            "BlueRoan" => "BlueRoan",
            "Dapple" => "Dapple",
            "Bay" => "Bay",
            "Belgian" => "Belgian",
            "Shire" => "Shire",
            "Chestnut" => "Chestnut",
            _ => "Roan"
        };

        /// <summary>Migrates horses that have AT texture keys but not our own skin key, e.g. from old saves.</summary>
        public static void MigrateAtSkinKeys(IMonitor monitor)
        {
            Utility.ForEachCharacter(character =>
            {
                if (character is Horse horse && !horse.modData.ContainsKey(HorseSkinKey))
                {
                    if (horse.modData.TryGetValue("AlternativeTextureVariation", out string? variation))
                    {
                        string skinName = variation switch
                        {
                            "0" => "Roan",
                            "1" => "Shire",
                            "2" => "Dapple",
                            "3" => "Bay",
                            "4" => "Belgian",
                            "5" => "BlueRoan",
                            "6" => "Chestnut",
                            _ => "Roan"
                        };
                        horse.modData[HorseSkinKey] = skinName;
                        monitor.Log($"Migrated horse '{horse.Name}' AT skin variation '{variation}' → '{skinName}'", LogLevel.Debug);
                    }
                }
                return true;
            });
        }
    }
}