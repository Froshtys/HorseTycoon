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
                // Stardew 1.6 overrode Stable tracking to utilize unique Guids mapped to the farm layout
                Guid newHorseGuid = Guid.NewGuid();

                // Spawn coordinates matching the stable structure's placement position
                int tileX = targetStable.tileX.Get() + 1;
                int tileY = targetStable.tileY.Get() + 1;

                // Instantiate the canonical 1.6 Horse character object
                activeHorse = new Horse(newHorseGuid, tileX, tileY);

                // Link the stable's architectural data profile to the newly spawned character Guid
                targetStable.HorseId = newHorseGuid;

                // Force register the character into the active farm simulation zone
                Game1.getFarm().characters.Add(activeHorse);
            }
            // 2. Handle Existing Hidden Horse (Only runs if a horse was already assigned)
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

            // 3. Hide and Assign the New Horse
            activeHorse.Name = selectedBarnHorse.Name;
            activeHorse.displayName = selectedBarnHorse.displayName;

            // Track internal placement variables using standard 1.6 properties
            selectedBarnHorse.modData["Froshty.HorseTycoon/CurrentStableId"] = targetStable.id.ToString();
            selectedBarnHorse.modData[HideKey] = "true";
            targetStable.modData[CurrentFarmHorseIdKey] = selectedBarnHorse.myID.Value.ToString(); // Use .Value for 1.6 netfields

            // Synchronize spatial ties
            targetStable.grabHorse();
            monitor.Log($"Save hidden horse with ID {selectedBarnHorse.myID}.", LogLevel.Debug);

            // 4. Visual Swapping
            string skinTag = selectedBarnHorse.skinID.Value ?? "0"; // Handle null-safety on NetStrings
            monitor.Log($"Horse Type Selected: {skinTag}", LogLevel.Info);

            string variation = skinTag switch
            {
                "RedRoan" => "0",
                "Shire" => "1",
                "Dapple" => "2",
                "Bay" => "3",
                "Belgian" => "4",
                "BlueRoan" => "5",
                "Chesnut" => "6",
                _ => "0"
            };

            SetHorseSkin(activeHorse, variation, monitor);

            // 5. Cache Sync for 1.6 Asset Pipeline
            helper.GameContent.InvalidateCache("Animals/Horse");
            monitor.Log($"Successfully swapped active mount to {activeHorse.Name}!", LogLevel.Info);
        }

        public static void ConvertStableHorseToFarmAnimal(Stable stable, Horse horse, Building barn, IMonitor monitor, IModHelper helper)
        {
            // 1. Generate unique ID via Game1
            long newId = Game1.Multiplayer.getNewID();
            FarmAnimal newHorse = new FarmAnimal("Ems.Horse", newId, Game1.player.UniqueMultiplayerID);

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

            // Force into Barn list (bypassing capacity)
            barn.GetIndoors().animals.Add(newHorse.myID.Value, newHorse);

            string skinTag = newHorse.skinID.ToString();
            monitor.Log($"Horse Type Selected: {skinTag}", LogLevel.Info);

            string variation = skinTag switch
            {
                "RedRoan" => "0",
                "Shire" => "1",
                "Dapple" => "2",
                "Bay" => "3",
                "Belgian" => "4",
                "BlueRoan" => "5",
                "Chesnut" => "6",
                _ => "0"
            };

            SetHorseSkin(horse, variation, monitor);
            helper.GameContent.InvalidateCache("Animals/Horse");

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

        private static void SetHorseSkin(Horse horse, string variation, IMonitor monitor)
        {
            const string AlternativeTextureOwner = "Froshty.HorseTycoonAT";
            const string AlternativeTextureName = "Froshty.HorseTycoonAT.Character_Horse";

            horse.modData["AlternativeTextureOwner"] = AlternativeTextureOwner;
            horse.modData["AlternativeTextureName"] = AlternativeTextureName;
            horse.modData["AlternativeTextureVariation"] = variation;

            foreach (var key in horse.modData.Keys)
            {
                {
                    monitor.Log($"Horse ModData Key: {key} | Value: {horse.modData[key]}", LogLevel.Info);
                }
            }

        }
    }
}