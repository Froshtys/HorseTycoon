using System.Runtime.CompilerServices;
using StardewValley;

namespace HorseTycoon.Models
{

    public class HorseStats
    {
        private readonly FarmAnimal Animal;


        public enum HorseSourceQuality
        {
            Starter,
            Special,
            Legendary
        }

        // ModData Keys
        private const string Prefix = "Froshty.HorseTycoon/";
        private const string IV_Suffix = "_IV"; // Genetic (0-50)
        private const string EV_Suffix = "_EV"; // Trained (0-50)

        // Individual Stat Keys
        public const string SpeedIVKey = Prefix + "Speed" + IV_Suffix;
        public const string SpeedEVKey = Prefix + "Speed" + EV_Suffix;

        public const string StaminaIVKey = Prefix + "Stamina" + IV_Suffix;
        public const string StaminaEVKey = Prefix + "Stamina" + EV_Suffix;

        public const string JumpIVKey = Prefix + "Jump" + IV_Suffix;
        public const string JumpEVKey = Prefix + "Jump" + EV_Suffix;

        public HorseStats(FarmAnimal animal) => this.Animal = animal;

        // --- Speed (Total Max 100) ---
        public int SpeedIV { get => GetStat(nameof(SpeedIV)); set => SetStat(nameof(SpeedIV), value); }
        public int SpeedEV { get => GetStat(nameof(SpeedEV)); set => SetStat(nameof(SpeedEV), value); }
        public int TotalSpeed => Math.Min(100, SpeedIV + SpeedEV);

        // --- Stamina (Total Max 100) ---
        public int StaminaIV { get => GetStat(nameof(StaminaIV)); set => SetStat(nameof(StaminaIV), value); }
        public int StaminaEV { get => GetStat(nameof(StaminaEV)); set => SetStat(nameof(StaminaEV), value); }
        public int TotalStamina => Math.Min(100, StaminaIV + StaminaEV);

        // --- Jump Distance (Total Max 100) ---
        public int JumpIV { get => GetStat(nameof(JumpIV)); set => SetStat(nameof(JumpIV), value); }
        public int JumpEV { get => GetStat(nameof(JumpEV)); set => SetStat(nameof(JumpEV), value); }
        public int TotalJump => Math.Min(100, JumpIV + JumpEV);

        // --- Internal Logic ---

        private int GetStat(string propertyName)
        {
            string key = MapPropertyToKey(propertyName);
            return Animal.modData.TryGetValue(key, out string val) && int.TryParse(val, out int result)
                ? Math.Clamp(result, 0, 50)
                : 0;
        }

        private void SetStat(string propertyName, int value)
        {
            string key = MapPropertyToKey(propertyName);
            Animal.modData[key] = Math.Clamp(value, 0, 50).ToString();
        }

        private string MapPropertyToKey(string propertyName)
        {
            // Maps "SpeedIV" to "Froshty.HorseTycoon/Speed_IV"
            // Maps "SpeedEV" to "Froshty.HorseTycoon/Speed_EV"
            string type = propertyName.EndsWith("IV") ? IV_Suffix : EV_Suffix;
            string stat = propertyName.Replace("IV", "").Replace("EV", "");
            return $"{Prefix}{stat}{type}";
        }

        public void RandomizeStats(HorseSourceQuality quality)
        {
            Random rand = new Random();

            // Define ranges based on quality
            (int min, int max) range = quality switch
            {
                HorseSourceQuality.Starter => (0, 25),
                HorseSourceQuality.Special => (15, 35),
                HorseSourceQuality.Legendary => (25, 50),
                _ => (0, 50)
            };

            // Apply the random roll within the range
            this.SpeedIV = rand.Next(range.min, range.max + 1);
            this.StaminaIV = rand.Next(range.min, range.max + 1);
            this.JumpIV = rand.Next(range.min, range.max + 1);

            // EVs always start at 0 for new horses
            this.SpeedEV = 0;
            this.StaminaEV = 0;
            this.JumpEV = 0;
        }
    }
}