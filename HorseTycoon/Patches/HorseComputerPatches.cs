using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HorseTycoon.Menus;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace HorseTycoon.Patches
{
    /// <summary>
    /// Horse Computer furniture: a desktop tower that, when used, opens the read-only
    /// <see cref="HorseComputerMenu"/> ledger of every horse on the farm.
    /// </summary>
    internal static class HorseComputerPatches
    {
        internal const string HorseComputerId = "(F)CP.HorseTycoon.HorseComputer";

        /// <summary>Tooltip text. Data/Furniture has no description field — vanilla
        /// <c>loadDescription</c> hands every normal piece a generic placement line — so a custom one
        /// has to be patched in (same as <see cref="MannequinPatches"/>).</summary>
        private const string ComputerDescription = "Keeps the records for every horse on the farm.";

        private static IModHelper? Helper;

        /// <summary>The mod's horse icon, used on the menu's stable tab. Loaded once on first open.</summary>
        private static Texture2D? HorseIcon;

        internal static void Initialize(IModHelper helper)
        {
            Helper = helper;
        }

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Furniture), nameof(Furniture.checkForAction), new[] { typeof(Farmer), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(HorseComputerPatches), nameof(Furniture_checkForAction_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(Furniture), "loadDescription"),
                postfix: new HarmonyMethod(typeof(HorseComputerPatches), nameof(Furniture_loadDescription_Postfix))
            );
        }

        internal static bool IsHorseComputer(Furniture furniture) =>
            furniture.QualifiedItemId == HorseComputerId;

        private static void Furniture_loadDescription_Postfix(Furniture __instance, ref string __result)
        {
            if (IsHorseComputer(__instance))
                __result = ComputerDescription;
        }

        private static bool Furniture_checkForAction_Prefix(Furniture __instance, Farmer who, bool justCheckingForActivity, ref bool __result)
        {
            if (justCheckingForActivity || !IsHorseComputer(__instance))
                return true;

            // Opens whatever the player is holding, like a furniture catalogue: picking the computer
            // back up is a left-click (GameLocation.LowPriorityLeftClick), so nothing is shadowed here.
            if (!HorseHelper.CanOpenMenu)
                return true;

            __result = true;

            List<FarmAnimal> horses = HorseHelper.GetAllBarnHorses()
                .OrderBy(h => h.Name)
                .ToList();

            // Opens even with no horses: the race-results tab is still worth reading, and each tab
            // draws its own empty state.
            Game1.playSound("bigSelect");
            HorseIcon ??= Helper?.ModContent.Load<Texture2D>("assets/horse_stats_icon.png");
            Game1.activeClickableMenu = new HorseComputerMenu(horses, HorseIcon);
            Logger.LogVerbose($"Horse Computer opened with {horses.Count} horse record(s).");
            return false;
        }
    }
}
