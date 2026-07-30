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

        // Set to the current day by the training potion (see TrainingPotionManager): while it's today's
        // date, every stat's daily requirement for that horse is halved.
        private const string TrainingBoostDateKey = "Froshty.HorseTycoon/TrainingBoostDate";
        private const double TrainingBoostMultiplier = 0.5;

        // Multiplayer: all stat mutations are applied authoritatively on the host so that the single
        // backing FarmAnimal's modData (counters + EVs) is owned/persisted by one peer. Farmhands report
        // their riding contributions to the host, which accumulates everyone's progress for the day.
        private const string MsgTraining = "HorseTycoon.Training";

        // Host -> rider: tells the player who earned the stat gain to show the level-up notification,
        // since the host applies the mutation but the rider (often a farmhand) needs to see the result.
        private const string MsgTrainingNotify = "HorseTycoon.TrainingNotify";

        // Farmhand -> host: a training potion was given to a horse. The requirement checks all run on the
        // host, so the host has to be the one to stamp the boost onto the horse's modData.
        private const string MsgTrainingBoost = "HorseTycoon.TrainingBoost";

        // Kind tags carried in the training message.
        private const string KindJump = "Jump";
        private const string KindSprint = "Sprint";
        private const string KindSpeed = "Speed";

        /// <param name="HorseId">The backing FarmAnimal's id (FarmAnimal.myID), stable across the network.</param>
        /// <param name="Kind">One of the Kind* tags.</param>
        /// <param name="Amount">Jumps/sprints performed, or pixels travelled, depending on Kind.</param>
        /// <param name="Day">Game1.Date.TotalDays when the message was sent; host discards if the day has advanced.</param>
        private record TrainingMessage(long HorseId, string Kind, float Amount, int Day);

        /// <param name="HorseName">Display name of the horse that improved.</param>
        /// <param name="StatName">The stat that improved (Jump/Speed/Sprint).</param>
        private record TrainingNotifyMessage(string HorseName, string StatName);

        /// <param name="HorseId">The backing FarmAnimal's id of the horse that drank a training potion.</param>
        private record TrainingBoostMessage(long HorseId);

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
                ApplyJumpProgress(horse, 1, Game1.player.UniqueMultiplayerID);
            else
                ReportToHost(horse.myID.Value, KindJump, 1f);
        }

        /// <summary>Credits sprint training. <paramref name="sprints"/> lets a well-played sprint minigame
        /// count for more than one; see <see cref="SprintMinigameManager"/>.</summary>
        public static void ProcessSprint(Horse mount, int sprints = 1)
        {
            if (sprints <= 0) return;

            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(mount);
            if (horse == null) return;

            if (Context.IsMainPlayer)
                ApplySprintProgress(horse, sprints, Game1.player.UniqueMultiplayerID);
            else
                ReportToHost(horse.myID.Value, KindSprint, sprints);
        }

        public static void ProcessMovement(Horse mount, float distanceTraveled)
        {
            FarmAnimal? horse = HorseHelper.GetFarmAnimalForHorse(mount);
            if (horse == null) return;

            if (Context.IsMainPlayer)
            {
                ApplyDistanceProgress(horse, distanceTraveled, Game1.player.UniqueMultiplayerID);
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

        private static void ApplyJumpProgress(FarmAnimal horse, int jumps, long riderId)
        {
            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(JumpDateKey, out string date) && date == today)
                return;

            stats.DailyJumps += jumps;

            if (stats.DailyJumps >= JumpsNeeded(horse, stats))
            {
                if (ApplyTraining(horse, "Jump", riderId))
                {
                    horse.modData[JumpDateKey] = today;
                    stats.DailyJumps = 0;
                }
            }
        }

        private static void ApplySprintProgress(FarmAnimal horse, int sprints, long riderId)
        {
            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(SprintDateKey, out string date) && date == today)
                return;

            stats.DailySprints += sprints;

            if (stats.DailySprints >= SprintsNeeded(horse, stats))
            {
                if (ApplyTraining(horse, "Sprint", riderId))
                {
                    horse.modData[SprintDateKey] = today;
                    stats.DailySprints = 0;
                }
            }
        }

        private static void ApplyDistanceProgress(FarmAnimal horse, float distanceTraveled, long riderId)
        {
            var stats = horse.GetHorseStats();
            string today = Game1.Date.TotalDays.ToString();

            if (horse.modData.TryGetValue(SpeedDateKey, out string date) && date == today)
                return;

            stats.DailyDistance += distanceTraveled;

            if (stats.DailyDistance >= DistanceNeeded(horse, stats))
            {
                if (ApplyTraining(horse, "Speed", riderId))
                {
                    horse.modData[SpeedDateKey] = today;
                    stats.DailyDistance = 0f;
                }
            }
        }

        // ----- Daily requirements -----
        // Each stat's requirement scales with the horse's current total in that stat (the better the
        // horse, the more work a further point costs), with a floor for untrained horses. A horse that
        // drank a training potion today only needs half of it in every stat.

        private static double JumpsNeeded(FarmAnimal horse, HorseStats stats) =>
            Math.Max(5, JumpsPerDayBase * (stats.TotalJump * 0.01)) * BoostMultiplier(horse);

        private static double SprintsNeeded(FarmAnimal horse, HorseStats stats) =>
            Math.Max(2, SprintsPerDayBase * (stats.TotalSprint * 0.01)) * BoostMultiplier(horse);

        /// <summary>Distance requirement in pixels (tiles needed * 64 pixels per tile).</summary>
        private static double DistanceNeeded(FarmAnimal horse, HorseStats stats) =>
            Math.Max(200, DistanceTilesPerDayBase * (stats.TotalSpeed * 0.01)) * 64 * BoostMultiplier(horse);

        private static double BoostMultiplier(FarmAnimal horse) =>
            HasTrainingBoost(horse) ? TrainingBoostMultiplier : 1.0;

        /// <summary>Whether this horse is under the training potion's effect today.</summary>
        public static bool HasTrainingBoost(FarmAnimal horse) =>
            horse != null
            && horse.modData.TryGetValue(TrainingBoostDateKey, out string date)
            && date == Game1.Date.TotalDays.ToString();

        /// <summary>Halves this horse's daily training requirements for the rest of today.</summary>
        public static void GrantTrainingBoost(FarmAnimal horse)
        {
            if (Context.IsMainPlayer)
            {
                horse.modData[TrainingBoostDateKey] = Game1.Date.TotalDays.ToString();
                return;
            }

            // Stamp it locally too so this player's own UI/checks agree, then let the host apply the
            // authoritative copy that the requirement checks actually read.
            horse.modData[TrainingBoostDateKey] = Game1.Date.TotalDays.ToString();
            Manager.Helper.Multiplayer.SendMessage(
                new TrainingBoostMessage(horse.myID.Value),
                MsgTrainingBoost,
                modIDs: new[] { Manager.Helper.ModRegistry.ModID });
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
            if (e.FromModID != Manager.Helper.ModRegistry.ModID) return;

            // Host -> rider: a farmhand whose riding earned a stat gain shows the level-up notification.
            if (e.Type == MsgTrainingNotify)
            {
                var notify = e.ReadAs<TrainingNotifyMessage>();
                ShowTrainingMessage(notify.HorseName, notify.StatName);
                return;
            }

            // Only the host owns the backing FarmAnimal's data; SendMessage never delivers to the sender.
            if (!Context.IsMainPlayer) return;

            if (e.Type == MsgTrainingBoost)
            {
                FarmAnimal? boosted = HorseHelper.GetHiddenHorseById(e.ReadAs<TrainingBoostMessage>().HorseId);
                if (boosted != null)
                    boosted.modData[TrainingBoostDateKey] = Game1.Date.TotalDays.ToString();
                return;
            }

            if (e.Type != MsgTraining) return;

            var msg = e.ReadAs<TrainingMessage>();
            if (msg.Day != Game1.Date.TotalDays) return;
            FarmAnimal? horse = HorseHelper.GetHiddenHorseById(msg.HorseId);
            if (horse == null) return;

            switch (msg.Kind)
            {
                case KindJump: ApplyJumpProgress(horse, (int)msg.Amount, e.FromPlayerID); break;
                case KindSprint: ApplySprintProgress(horse, (int)msg.Amount, e.FromPlayerID); break;
                case KindSpeed: ApplyDistanceProgress(horse, msg.Amount, e.FromPlayerID); break;
            }
        }

        private static bool ApplyTraining(FarmAnimal horse, string statName, long riderId)
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

            // Notify the rider who earned the gain. If that's the host, show it locally; otherwise send
            // a message so the farmhand on the horse, not the host applying the data, sees the result.
            if (riderId == Game1.player.UniqueMultiplayerID)
            {
                ShowTrainingMessage(horse.Name, statName);
            }
            else
            {
                Manager.Helper.Multiplayer.SendMessage(
                    new TrainingNotifyMessage(horse.Name, statName),
                    MsgTrainingNotify,
                    modIDs: new[] { Manager.Helper.ModRegistry.ModID },
                    playerIDs: new[] { riderId });
            }
            return true;
        }

        private static void ShowTrainingMessage(string horseName, string statName)
        {
            Game1.showGlobalMessage($"{horseName} has improved their {statName}!");
            Game1.playSound("Pickup_Coin15");
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
