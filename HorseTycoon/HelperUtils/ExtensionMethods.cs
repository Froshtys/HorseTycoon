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

        private const int thinHorseXOffset = 12;

        internal static bool MouseOrPlayerIsInRange(this Character chara, Farmer who, int mouseX, int mouseY, bool ignoreMousePosition)
        {
            if (!ignoreMousePosition)
            {
                int mouseMargin = 44;
                int charYOffset = 40;

                var charX = chara.StandingPixel.X;

                if (chara is Horse)
                {
                    mouseMargin = 70;

                    charX -= thinHorseXOffset;

                }
                else if (chara is Pet)
                {
                    charYOffset = 24;
                }

                return Utility.distance(mouseX, charX, mouseY, chara.StandingPixel.Y - charYOffset) <= mouseMargin;
            }
            else
            {
                var playerPos = who.StandingPixel;
                var charaPos = chara.StandingPixel;

                var charX = charaPos.X;

                charX -= thinHorseXOffset;


                int xDistance = Math.Abs(playerPos.X - charX);
                int yDistance = Math.Abs(playerPos.Y - charaPos.Y);

                return who.FacingDirection switch
                {
                    Game1.up => playerPos.Y > charaPos.Y && xDistance < 48,
                    Game1.down => playerPos.Y < charaPos.Y && xDistance < 48,
                    Game1.right => playerPos.X < charaPos.X && yDistance < 48,
                    Game1.left => playerPos.X > charaPos.X && yDistance < 48,
                    _ => false,
                };
            }
        }
    }
}