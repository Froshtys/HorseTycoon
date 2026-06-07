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
        public int SpeedIV { get => GetStat(nameof(SpeedIV)); set => SetStat(nameof(SpeedIV), value); }
        public int SpeedEV { get => GetStat(nameof(SpeedEV)); set => SetStat(nameof(SpeedEV), value); }
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
                    >= 80 and < 95 => 5, // 3 Tiles
                    >= 95 => 6 // 4 Tiles
                };
            }
        }

        public float SpeedBoost { get { return this.TotalSpeed / 25f; } }

        // --- Sprint (Total Max 100) ---
        public int SprintIV { get => GetStat(nameof(SprintIV)); set => SetStat(nameof(SprintIV), value); }
        public int SprintEV { get => GetStat(nameof(SprintEV)); set => SetStat(nameof(SprintEV), value); }
        public int TotalSprint => Math.Min(STAT_MAX, SprintIV + SprintEV);

        // --- Jump Distance (Total Max 100) ---
        public int JumpIV { get => GetStat(nameof(JumpIV)); set => SetStat(nameof(JumpIV), value); }
        public int JumpEV { get => GetStat(nameof(JumpEV)); set => SetStat(nameof(JumpEV), value); }
        public int TotalJump => Math.Min(STAT_MAX, JumpIV + JumpEV);

        // ModData Keys
        public const string DailyJumpsKey = Prefix + "DailyJumps";
        public const string DailySprintsKey = Prefix + "DailySprints";
        public const string DailyDistanceKey = Prefix + "DailyDistance";

        // --- Training Progress Properties ---
        public int DailyJumps { get => Animal.modData.TryGetValue(DailyJumpsKey, out string val) && int.TryParse(val, out int result) ? result : 0; set => Animal.modData[DailyJumpsKey] = value.ToString(); }
        public int DailySprints { get => Animal.modData.TryGetValue(DailySprintsKey, out string val) && int.TryParse(val, out int result) ? result : 0; set => Animal.modData[DailySprintsKey] = value.ToString(); }
        public float DailyDistance { get => Animal.modData.TryGetValue(DailyDistanceKey, out string val) && float.TryParse(val, out float result) ? result : 0f; set => Animal.modData[DailyDistanceKey] = value.ToString(); }

        private int GetStat(string propertyName)
        {
            string key = MapPropertyToKey(propertyName);
            if (Animal.modData.TryGetValue(key, out string val) && int.TryParse(val, out int result))
            {
                // IVs are only in 10 increments
                if (propertyName.EndsWith("IV"))
                {
                    result = (int)Math.Round(result / 10.0) * 10;
                }
                return Math.Clamp(result, 0, 50);
            }
            return 0;
        }

        private void SetStat(string propertyName, int value)
        {
            string key = MapPropertyToKey(propertyName);

            // IVs are only in 10 increments
            if (propertyName.EndsWith("IV"))
            {
                value = (int)Math.Round(value / 10.0) * 10;
            }

            Animal.modData[key] = Math.Clamp(value, 0, 50).ToString();
        }

        private string MapPropertyToKey(string propertyName)
        {
            string type = propertyName.EndsWith("IV") ? IV_Suffix : EV_Suffix;
            string stat = propertyName.Replace("IV", "").Replace("EV", "");
            return $"{Prefix}{stat}{type}";
        }

        public void RandomizeStats(HorseSourceQuality quality)
        {
            Random rand = new Random();

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