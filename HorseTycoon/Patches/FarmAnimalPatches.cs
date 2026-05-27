using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Menus;

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

            // --- Menu Confirm Interceptor ---
            harmony.Patch(
                original: AccessTools.Method(typeof(AnimalQueryMenu), nameof(AnimalQueryMenu.receiveLeftClick)),
                postfix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(AnimalQueryMenu_receiveLeftClick_Postfix))
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

        public static void AnimalQueryMenu_receiveLeftClick_Postfix(AnimalQueryMenu __instance, int x, int y)
        {
            // 1. Confirm the "Yes" confirmation button for selling was clicked
            if (__instance.confirmingSell && __instance.yesButton != null && __instance.yesButton.containsPoint(x, y))
            {

                FarmAnimal? animal = __instance.animal;
                if (animal != null && animal.type.Value != null && animal.type.Value.Contains("Horse"))
                {
                    long soldId = animal.myID.Value;

                    // This ensures it completely disappears from the game's global collections and animal listings.
                    Utility.ForEachLocation(location =>
                    {
                        if (location.animals.ContainsKey(soldId))
                        {
                            location.animals.Remove(soldId);
                        }

                        if (location is AnimalHouse barnInterior && barnInterior.animals.ContainsKey(soldId))
                        {
                            barnInterior.animals.Remove(soldId);
                        }
                        return true;
                    });

                    // Scan all stables to safely unlink the physical entity
                    foreach (Stable stable in Game1.getFarm().buildings.OfType<Stable>())
                    {
                        if (stable.modData.TryGetValue(HorseHelper.CurrentFarmHorseIdKey, out string linkedId) && linkedId == soldId.ToString())
                        {
                            stable.modData.Remove(HorseHelper.CurrentFarmHorseIdKey);

                            Horse stableHorse = stable.getStableHorse();
                            if (stableHorse != null)
                            {
                                Game1.currentLocation?.characters.Remove(stableHorse);
                                stableHorse.currentLocation?.characters.Remove(stableHorse);
                            }
                            stable.HorseId = Guid.Empty;
                            break;
                        }
                    }
                }
            }
        }
    }
}