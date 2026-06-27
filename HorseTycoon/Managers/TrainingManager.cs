using HorseTycoon.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon
{
    public static class TrainingManager
    {
        private static JumpManager Manager = null!;

        // Stat-specific date keys to allow training all 3 in one day
        private const string JumpDateKey = "Froshty.HorseTycoon/JumpTrainedDate";
        private const string SpeedDateKey = "Froshty.HorseTycoon/SpeedTrainedDate";
        private const string SprintDateKey = "Froshty.HorseTycoon/SprintTrainedDate";

        private const int SprintsPerDayBase = 20;
        private const int JumpsPerDayBase = 40;
        private const int DistanceTilesPerDayBase = 2000;

        // Multiplayer: all stat mutations are applied authoritatively on the host so that the single
        // backing FarmAnimal's modData (counters + EVs) is owned/persisted by one peer. Farmhands report
        // their riding contributions to the host, which accumulates everyone's progress for the day.
        private const string MsgTraining = "HorseTycoon.Training";

        // Kind tags carried in the training message.
        private const string KindJump = "Jump";
        private const string KindSprint = "Sprint";
        private const string KindSpeed = "Speed";

        /// <param name="HorseId">The backing FarmAnimal's id (FarmAnimal.myID), stable across the network.</param>
        /// <param name="Kind">One of the Kind* tags.</param>
        /// <param name="Amount">Jumps/sprints performed, or pixels travelled, depending on Kind.</param>
        /// <param name="Day">Game1.Date.TotalDays when the message was sent; host discards if the day has advanced.</param>
        private record TrainingMessage(long HorseId, string Kind, float Amount, int Day);

        // Farmhands batch distance and flush it to the host periodically rather than messaging every tick.
        private const float DistanceFlushChunk = 64f * 5f; // 5 tiles
        private const int DistanceFlushTicks = 60;         // ...or at least once per second
        private static readonly PerScreen<float> pendingDistance = new(() => 0f);
        private static readonly PerScreen<int> lastDistanceFlushTick = new(() => 0);

        public static void Initialize(JumpManager manager)
        {
            Manager = manager;
            Manager.Helper.Events.Multiplayer.ModMessageReceived += OnMessageReceived;
        }

        /// <summary>
        /// Resets every horse's daily training progress counters. Called at the start of each day so
        /// partial progress from the previous day doesn't carry over (which would otherwise let a horse
        /// complete training from a single jump/step the next morning).
        /// </summary>
        public static void ResetDailyCounters()
        {
            foreach (FarmAnimal horse in HorseHelper.GetAllBarnHorses())
            {
                var stats = horse.GetHorseStats();
                stats.DailyJumps = 0;
                stats.DailySprints = 0;
                stats.DailyDistance = 0f;
            }
            pendingDistance.Value = 0f;
        }

        public static void ProcessJump(Horse mount)
        {
            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(mount);
            if (horse == null) return;

            if (Context.IsMainPlayer)
                ApplyJumpProgress(horse, 1);
            else
                ReportToHost(horse.myID.Value, KindJump, 1f);
        }

        public static void ProcessSprint(Horse mount)
        {
            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(mount);
            if (horse == null) return;

            if (Context.IsMainPlayer)
                ApplySprintProgress(horse, 1);
            else
                ReportToHost(horse.myID.Value, KindSprint, 1f);
        }

        public static void ProcessMovement(Horse mount, float distanceTraveled)
        {
            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(mount);
            if (horse == null) return;

            if (Context.IsMainPlayer)
            {
                ApplyDistanceProgress(horse, distanceTraveled);
                return;
            }

            // Farmhand: batch distance and flush to the host periodically to avoid per-tick messaging.
            pendingDistance.Value += distanceTraveled;
            if (pendingDistance.Value >= DistanceFlushChunk
                || Game1.ticks - lastDistanceFlushTick.Value >= DistanceFlushTicks)
            {
                ReportToHost(horse.myID.Value, KindSpeed, pendingDistance.Value);
                pendingDistance.Value = 0f;
                lastDistanceFlushTick.Value = Game1.ticks;
            }
        }

        // ----- Host-authoritative progress application -----

        private static void ApplyJumpProgress(FarmAnimal horse, int jumps)
        {
            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(JumpDateKey, out string date) && date == today)
                return;

            stats.DailyJumps += jumps;

            if (stats.DailyJumps >= Math.Max(5, JumpsPerDayBase * (stats.TotalJump * 0.01)))
            {
                if (ApplyTraining(horse, "Jump"))
                {
                    horse.modData[JumpDateKey] = today;
                    stats.DailyJumps = 0;
                }
            }
        }

        private static void ApplySprintProgress(FarmAnimal horse, int sprints)
        {
            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(SprintDateKey, out string date) && date == today)
                return;

            stats.DailySprints += sprints;

            if (stats.DailySprints >= Math.Max(2, SprintsPerDayBase * (stats.TotalSprint * 0.01)))
            {
                if (ApplyTraining(horse, "Sprint"))
                {
                    horse.modData[SprintDateKey] = today;
                    stats.DailySprints = 0;
                }
            }
        }

        private static void ApplyDistanceProgress(FarmAnimal horse, float distanceTraveled)
        {
            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(SpeedDateKey, out string date) && date == today)
                return;

            stats.DailyDistance += distanceTraveled;

            // DistanceTilesPerDayNeeded tiles * 64 pixels per tile
            if (stats.DailyDistance >= Math.Max(200, DistanceTilesPerDayBase * (stats.TotalSpeed * 0.01)) * 64)
            {
                if (ApplyTraining(horse, "Speed"))
                {
                    horse.modData[SpeedDateKey] = today;
                    stats.DailyDistance = 0f;
                }
            }
        }

        private static void ReportToHost(long horseId, string kind, float amount)
        {
            Manager.Helper.Multiplayer.SendMessage(
                new TrainingMessage(horseId, kind, amount, Game1.Date.TotalDays),
                MsgTraining,
                modIDs: new[] { Manager.Helper.ModRegistry.ModID });
        }

        private static void OnMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            // Only the host owns the backing FarmAnimal's data; SendMessage never delivers to the sender.
            if (!Context.IsMainPlayer || e.Type != MsgTraining) return;
            if (e.FromModID != Manager.Helper.ModRegistry.ModID) return;

            var msg = e.ReadAs<TrainingMessage>();
            if (msg.Day != Game1.Date.TotalDays) return;
            FarmAnimal? horse = HorseHelper.GetHiddenHorseById(msg.HorseId);
            if (horse == null) return;

            switch (msg.Kind)
            {
                case KindJump: ApplyJumpProgress(horse, (int)msg.Amount); break;
                case KindSprint: ApplySprintProgress(horse, (int)msg.Amount); break;
                case KindSpeed: ApplyDistanceProgress(horse, msg.Amount); break;
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
