using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using HorseTycoon.Menus;
using static StardewValley.GameStateQuery;
using StardewModdingAPI;

namespace HorseTycoon.Patches
{
    public class MenuPatches
    {
        public static ClickableTextureComponent? StatsButton;

        private static IModHelper? Helper;
        private static Texture2D? StatsIconTexture;

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            Helper = helper;
            StatsIconTexture = Helper.ModContent.Load<Texture2D>("assets/horse_stats_icon.png");
        }
        public static void Apply(Harmony harmony)
        {

            harmony.Patch(
                original: AccessTools.Method(typeof(AnimalQueryMenu), nameof(AnimalQueryMenu.receiveLeftClick)),
                prefix: new HarmonyMethod(typeof(MenuPatches), nameof(ReceiveLeftClick_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Constructor(typeof(AnimalQueryMenu), new[] { typeof(FarmAnimal) }),
                postfix: new HarmonyMethod(typeof(MenuPatches), nameof(Constructor_Postfix))
            );
        }

        private static void Constructor_Postfix(AnimalQueryMenu __instance, FarmAnimal animal)
        {
            if (animal == null || !animal.type.Value.Contains("Horse"))
            {
                StatsButton = null;
                return;
            }

            // X: 72 pixels to the left of the menu box
            int x = __instance.xPositionOnScreen - 68;

            // Y: Start at the top of the menu and move down 128 pixels 
            // (This should put it roughly level with the name box)
            int y = __instance.yPositionOnScreen + 218;

            StatsButton = new ClickableTextureComponent(
                new Rectangle(x, y, 64, 64),
                StatsIconTexture,
                new Rectangle(0, 0, 16, 16),
                4f
            )
            {
                myID = 150,
                hoverText = "View Horse Stats"
            };

        }

        private static bool ReceiveLeftClick_Prefix(AnimalQueryMenu __instance, int x, int y)
        {
            if (StatsButton != null && StatsButton.containsPoint(x, y))
            {
                Game1.playSound("shwip");
                Game1.activeClickableMenu = new HorseStatsMenu(__instance.animal);
                return false; // Eat the click
            }
            return true;
        }
    }
}