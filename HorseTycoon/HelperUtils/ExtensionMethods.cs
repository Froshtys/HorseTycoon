using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;

namespace HorseTycoon
{
    internal static class ExtensionMethods
    {
        public static bool IsTractor(this Horse horse)
        {
            return horse != null && horse.modData.ContainsKey("Pathoschild.TractorMod");
        }

        public static bool IsTractorGarage(this Stable stable)
        {
            return stable != null && stable.buildingType.Value == "Pathoschild.TractorMod_Stable";
        }

        public static bool IsAboutEqualTo(this float firstValue, float secondValue)
        {
            return firstValue >= (secondValue - 0.1f) && firstValue <= (secondValue + 0.1f);
        }

    }
}