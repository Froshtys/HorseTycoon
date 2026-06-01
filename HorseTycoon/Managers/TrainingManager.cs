using HorseTycoon.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon
{
    public static class TrainingManager
    {
        private static JumpManager Manager;

        // Stat-specific date keys to allow training all 3 in one day
        private const string JumpDateKey = "Froshty.HorseTycoon/JumpTrainedDate";
        private const string SpeedDateKey = "Froshty.HorseTycoon/SpeedTrainedDate";
        private const string SprintDateKey = "Froshty.HorseTycoon/SprintTrainedDate";

        private const int SprintsPerDayBase = 20;
        private const int JumpsPerDayBase = 30;
        private const int DistanceTilesPerDayBase = 1000;


        public static void Initialize(JumpManager manager)
        {
            Manager = manager;
        }

        public static void ProcessJump(Horse mount)
        {
            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(mount);
            if (horse == null) return;

            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(JumpDateKey, out string date) && date == today)
                return;

            stats.DailyJumps++;

            if (stats.DailyJumps >= Math.Max(5, JumpsPerDayBase * (stats.TotalJump * 0.01)))
            {
                if (ApplyTraining(horse, "Jump"))
                {
                    horse.modData[JumpDateKey] = today;
                    stats.DailyJumps = 0;
                }
            }
        }

        public static void ProcessSprint(Horse mount)
        {
            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(mount);
            if (horse == null) return;

            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(SprintDateKey, out string date) && date == today)
                return;

            stats.DailySprints++;

            if (stats.DailySprints >= Math.Max(2, SprintsPerDayBase * (stats.TotalSprint * 0.01)))
            {
                if (ApplyTraining(horse, "Sprint"))
                {
                    horse.modData[SprintDateKey] = today;
                    stats.DailySprints = 0;
                }
            }
        }

        public static void ProcessMovement(Horse mount, float distanceTraveled)
        {
            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(mount);
            if (horse == null) return;

            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(SpeedDateKey, out string date) && date == today)
                return;

            stats.DailyDistance += distanceTraveled;

            // DistanceTilesPerDayNeeded tiles * 64 pixels per tile
            if (stats.DailyDistance >= Math.Max(150, DistanceTilesPerDayBase * (stats.TotalSpeed * 0.01)) * 64)
            {
                if (ApplyTraining(horse, "Speed"))
                {
                    horse.modData[SpeedDateKey] = today;
                    stats.DailyDistance = 0f;
                }
            }
        }

        private static bool ApplyTraining(FarmAnimal horse, string statName)
        {
            var stats = horse.GetHorseStats();

            int currentEv = statName switch
            {
                "Jump" => stats.JumpEV,
                "Speed" => stats.SpeedEV,
                "Sprint" => stats.SprintEV,
                _ => 50
            };

            if (currentEv >= HorseStats.EV_MAX)
            {
                Manager.Monitor.Log($"{horse.Name}'s {statName} is capped at {HorseStats.EV_MAX} EVs for current friendship.", LogLevel.Debug);
                return false;
            }

            switch (statName)
            {
                case "Jump": stats.JumpEV++; break;
                case "Speed": stats.SpeedEV++; break;
                case "Sprint": stats.SprintEV++; break;
                default: return false;
            }

            Game1.showGlobalMessage($"{horse.Name} has improved their {statName}!");
            Game1.playSound("Pickup_Coin15");
            return true;
        }

        public static bool HasTrainedSpeedToday(FarmAnimal horse)
        {
            if (horse == null) return false;
            string today = Game1.Date.TotalDays.ToString();
            return horse.modData.TryGetValue(SpeedDateKey, out string date) && date == today;
        }

        public static bool HasTrainedSprintToday(FarmAnimal horse)
        {
            if (horse == null) return false;
            string today = Game1.Date.TotalDays.ToString();
            return horse.modData.TryGetValue(SprintDateKey, out string date) && date == today;
        }

        public static bool HasTrainedJumpToday(FarmAnimal horse)
        {
            if (horse == null) return false;
            string today = Game1.Date.TotalDays.ToString();
            return horse.modData.TryGetValue(JumpDateKey, out string date) && date == today;
        }
    }
}