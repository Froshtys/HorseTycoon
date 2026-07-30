using System;
using System.Runtime.CompilerServices;
using StardewValley;

namespace HorseTycoon.Models
{
    public class HorseStats
    {
        private readonly FarmAnimal Animal;
        public enum HorseSourceQuality { Starter, Special, Legendary }

        // ModData Keys
        private const string Prefix = "Froshty.HorseTycoon/";
        private const string IV_Suffix = "_IV"; // Genetic (0-50)
        private const string EV_Suffix = "_EV"; // Trained (0-50)

        public const int IV_MAX = 50;
        public const int EV_MAX = 50;
        public const int STAT_MAX = IV_MAX + EV_MAX;

        // Individual Stat Keys
        public const string SpeedIVKey = Prefix + "Speed" + IV_Suffix;
        public const string SpeedEVKey = Prefix + "Speed" + EV_Suffix;
        public const string SprintIVKey = Prefix + "Sprint" + IV_Suffix;
        public const string SprintEVKey = Prefix + "Sprint" + EV_Suffix;
        public const string JumpIVKey = Prefix + "Jump" + IV_Suffix;
        public const string JumpEVKey = Prefix + "Jump" + EV_Suffix;

        public HorseStats(FarmAnimal animal) => this.Animal = animal;

        // --- Speed (Total Max 100) ---
        public int SpeedIV { get => GetStat(SpeedIVKey, isIV: true); set => SetStat(SpeedIVKey, value, isIV: true); }
        public int SpeedEV { get => GetStat(SpeedEVKey, isIV: false); set => SetStat(SpeedEVKey, value, isIV: false); }
        public int TotalSpeed => Math.Min(STAT_MAX, SpeedIV + SpeedEV);

        public int JumpDistance
        {
            get
            {
                int skill = this.TotalJump;
                return skill switch
                {
                    < 20 => 2, // No Tiles
                    >= 20 and < 50 => 3, // 1 tile
                    >= 50 and < 80 => 4, // 2 Tiles
                    >= 80 and < 100 => 5, // 3 Tiles
                    >= 100 => 6 // 4 Tiles
                };
            }
        }

        /// <summary>How much additive movement speed one point of the Speed stat is worth. Anything that
        /// wants to be priced "in skill points" (see <see cref="CarrotSpeedBonus"/>) should use this.</summary>
        public const float SpeedPerStatPoint = 1f / 40f;

        public float SpeedBoost { get { return this.TotalSpeed * SpeedPerStatPoint; } }

        /// <summary>Race speed bonus for a horse that ate a Carrot today (vanilla's own feeding, done at the
        /// stable before setting off): exactly one Speed stat point's worth, and applied alongside
        /// <see cref="SpeedBoost"/> (i.e. before the race's overall speed penalty) so it really is worth the
        /// same as training that point.</summary>
        public const float CarrotSpeedBonus = SpeedPerStatPoint;

        // --- Sprint (Total Max 100) ---
        public int SprintIV { get => GetStat(SprintIVKey, isIV: true); set => SetStat(SprintIVKey, value, isIV: true); }
        public int SprintEV { get => GetStat(SprintEVKey, isIV: false); set => SetStat(SprintEVKey, value, isIV: false); }
        public int TotalSprint => Math.Min(STAT_MAX, SprintIV + SprintEV);

        // --- Sprint formula (shared by ModEntry buff, festival player, and NPC racers) ---
        // Static + raw int so NPC racers (no HorseStats) can call it too. Tune here, changes everywhere.
        public const int SprintMinDurationMs = 1000;
        public const int SprintMaxDurationMs = 10000;

        /// <summary>Sprint length in ms for a total Sprint stat.</summary>
        public static int SprintDurationMs(int totalSprint) =>
            Math.Clamp(SprintMinDurationMs + (totalSprint * 50), SprintMinDurationMs, SprintMaxDurationMs);

        /// <summary>Additive sprint speed bonus in getMovementSpeed units (~1 tile/sec per point).</summary>
        public static float SprintSpeedBonus(int totalSprint) =>
            1f + (totalSprint / 10 * 0.1f);

        // --- Sprint minigame formulas (ModConfig.UseSprintMinigame) ---
        // The timing-bar sprint: starts at the base bonus, and every well-timed press stacks another
        // hit bonus on top. Stat no longer sets the speed directly; it sets how many attempts you get
        // and how forgiving each one is.

        /// <summary>Speed bonus the sprint opens at, before any timing hits.</summary>
        public const float MinigameBaseSpeedBonus = 1f;

        /// <summary>Extra speed bonus added by each well-timed press.</summary>
        public const float MinigameHitSpeedBonus = 0.25f;

        /// <summary>Timing attempts per sprint: one per 10 points of total Sprint, with a floor of one so a
        /// green horse still gets a go. Stat 0 and 10 both give 1 attempt; stat 100 gives 10.</summary>
        public static int MinigameChances(int totalSprint) =>
            Math.Max(1, Math.Clamp(totalSprint, 0, STAT_MAX) / 10);

        /// <summary>At or above this total Sprint, a rider who never touches the meter loses the coast at the
        /// end of the sprint. High-stat horses get so many attempts that an unplayed sprint drags on, and
        /// there's no reason to pay out the bonus second for not engaging with it.</summary>
        public const int MinigameNoCoastMinSprint = 40;

        /// <summary>How long the bar takes to cross the track once. Faster horses sweep quicker: every extra
        /// attempt the stat buys also shaves 50ms off each pass, so 1.30s at stat 0 down to 0.85s at stat 100.
        /// This keeps a long attempt chain from dragging, but note it also shrinks the hit window in real
        /// time, since <see cref="MinigameWindowHalf"/> is a fraction of the track rather than a duration.</summary>
        public static int MinigameSweepMs(int chances) =>
            Math.Max(500, 1300 - (50 * (chances - 1)));

        /// <summary>Half-width of the hit window, as a fraction of the track. Training (EV) widens it,
        /// breeding (IV) narrows it: a strong bloodline gives more attempts but demands sharper timing.
        /// Ranges from 0.020 (IV 50 / EV 0) through 0.054 (20/20) to 0.090 (IV 0 / EV 50).</summary>
        public static float MinigameWindowHalf(int sprintIV, int sprintEV) =>
            Math.Clamp(
                0.050f + (Math.Clamp(sprintEV, 0, EV_MAX) * 0.0008f) - (Math.Clamp(sprintIV, 0, IV_MAX) * 0.0006f),
                0.020f, 0.090f);

        // --- Warrior Energy formula (festival shrine pickup) ---
        public const float WarriorEnergyMinBonus = 1f;
        public const float WarriorEnergyMaxBonus = 5f;

        /// <summary>Additive Warrior Energy speed bonus for a total Speed stat: +0.5 per 10 points below 90,
        /// clamped to 1..5 (90+ = 1.0, 80 = 1.5, 70 = 2.0, ... 10 and below = 5.0): the slower the horse,
        /// the more the pickup is worth.</summary>
        public static float WarriorEnergyBonus(int totalSpeed) =>
            Math.Clamp((STAT_MAX + 10 - totalSpeed) / 20f, WarriorEnergyMinBonus, WarriorEnergyMaxBonus);

        // --- Jump Distance (Total Max 100) ---
        public int JumpIV { get => GetStat(JumpIVKey, isIV: true); set => SetStat(JumpIVKey, value, isIV: true); }
        public int JumpEV { get => GetStat(JumpEVKey, isIV: false); set => SetStat(JumpEVKey, value, isIV: false); }
        public int TotalJump => Math.Min(STAT_MAX, JumpIV + JumpEV);

        // ModData Keys
        public const string DailyJumpsKey = Prefix + "DailyJumps";
        public const string DailySprintsKey = Prefix + "DailySprints";
        public const string DailyDistanceKey = Prefix + "DailyDistance";

        // --- Training Progress Properties ---
        public int DailyJumps { get => Animal.modData.TryGetValue(DailyJumpsKey, out string val) && int.TryParse(val, out int result) ? result : 0; set => Animal.modData[DailyJumpsKey] = value.ToString(); }
        public int DailySprints { get => Animal.modData.TryGetValue(DailySprintsKey, out string val) && int.TryParse(val, out int result) ? result : 0; set => Animal.modData[DailySprintsKey] = value.ToString(); }
        public float DailyDistance { get => Animal.modData.TryGetValue(DailyDistanceKey, out string val) && float.TryParse(val, out float result) ? result : 0f; set => Animal.modData[DailyDistanceKey] = value.ToString(); }

        private int GetStat(string key, bool isIV)
        {
            if (Animal.modData.TryGetValue(key, out string val) && int.TryParse(val, out int result))
            {
                // IVs are only in 10 increments
                if (isIV)
                    result = (int)Math.Round(result / 10.0) * 10;
                return Math.Clamp(result, 0, isIV ? IV_MAX : EV_MAX);
            }
            return 0;
        }

        private void SetStat(string key, int value, bool isIV)
        {
            // IVs are only in 10 increments
            if (isIV)
                value = (int)Math.Round(value / 10.0) * 10;

            Animal.modData[key] = Math.Clamp(value, 0, isIV ? IV_MAX : EV_MAX).ToString();
        }

        public void RandomizeStats(HorseSourceQuality quality)
        {
            Random rand = Game1.random;

            // Maps out specific tiered multiplier step boundaries (MinMultiplier, MaxMultiplier)
            var range = quality switch
            {
                // Rolls options: 0, 10, 20
                HorseSourceQuality.Starter => (min: 0, max: 2),
                // Rolls options: 20, 30, 40
                HorseSourceQuality.Special => (min: 2, max: 4),
                // Rolls options: 40, 50
                HorseSourceQuality.Legendary => (min: 4, max: 5),
                _ => (min: 0, max: 5)
            };

            // Generate initial temporary multiplier chunks
            int speedMult = rand.Next(range.min, range.max + 1);
            int sprintMult = rand.Next(range.min, range.max + 1);
            int jumpMult = rand.Next(range.min, range.max + 1);

            // Prevent starter horse from having 0 in all stats
            if (quality == HorseSourceQuality.Starter)
            {
                int statToUpdate = rand.Next(0, 3);
                if (speedMult == 0 && sprintMult == 0 && jumpMult == 0)
                {
                    switch (statToUpdate)
                    {
                        case 0: speedMult = 1; break;
                        case 1: sprintMult = 1; break;
                        case 2: jumpMult = 1; break;
                    }
                }
            }

            this.SpeedIV = speedMult * 10;
            this.SprintIV = sprintMult * 10;
            this.JumpIV = jumpMult * 10;

            // EVs always start at 0 for new horses
            this.SpeedEV = 0;
            this.SprintEV = 0;
            this.JumpEV = 0;
        }

        public bool ApplyDebugStat(string stat, string type, int value)
        {
            value = Math.Clamp(value, 0, 50);
            type = type.ToLower();
            stat = stat.ToLower();

            switch (stat)
            {
                case "jump":
                    if (type == "iv") this.JumpIV = value;
                    else if (type == "ev") this.JumpEV = value;
                    else return false;
                    break;
                case "speed":
                    if (type == "iv") this.SpeedIV = value;
                    else if (type == "ev") this.SpeedEV = value;
                    else return false;
                    break;
                case "sprint":
                    if (type == "iv") this.SprintIV = value;
                    else if (type == "ev") this.SprintEV = value;
                    else return false;
                    break;
                default:
                    return false;
            }
            return true;
        }
    }
}