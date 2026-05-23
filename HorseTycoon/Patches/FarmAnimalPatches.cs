using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// Harmony patches to completely "disable" a FarmAnimal by hiding it, 
    /// stopping its AI, and removing its physical collision.
    /// </summary>
    public class FarmAnimalPatches
    {

        public static void Apply(Harmony harmony)
        {
            // Patch Draw
            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.draw), new[] { typeof(SpriteBatch) }),
                prefix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(Draw_Prefix))
            );

            // Patch Update logic
            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.updateWhenCurrentLocation)),
                prefix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(Update_Prefix))
            );

            // Patch Collision
            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.GetBoundingBox)),
                prefix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(GetBoundingBox_Prefix))
            );
        }

        // --- Patch Implementations ---

        private static bool Draw_Prefix(FarmAnimal __instance)
        {
            return !HorseHelper.IsHidden(__instance);
        }

        private static bool Update_Prefix(FarmAnimal __instance, GameTime time, GameLocation location)
        {
            return !HorseHelper.IsHidden(__instance);
        }

        private static bool GetBoundingBox_Prefix(FarmAnimal __instance, ref Rectangle __result)
        {
            if (HorseHelper.IsHidden(__instance))
            {
                __result = new Rectangle(-9999, -9999, 0, 0);
                return false;
            }
            return true;
        }
    }
}