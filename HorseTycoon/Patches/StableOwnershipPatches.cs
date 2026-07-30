using HarmonyLib;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;

namespace HorseTycoon.Patches
{
    /// <summary>
    /// Keeps vanilla's horse-ownership bookkeeping from renaming this mod's stable horses.
    /// <para>Vanilla treats a player as having exactly one horse whose name IS <see cref="Farmer.horseName"/>:
    /// <c>Stable.updateHorseOwnership</c> pushes the stable's owner onto the horse and then overwrites the
    /// horse's name with the owner's <c>horseName</c> (or "" when that is null). Our horses are named
    /// individually from their backing barn FarmAnimal, and <c>Game1.UpdateHorseOwnership</c> runs that
    /// method host-side every single morning, so left alone it renames every stable horse on the farm.</para>
    /// <para>The prefix keeps the ownership half (which is what the Horse Flute resolves against — see
    /// <c>FarmerTeam.OnRequestHorseWarp</c>) and drops the renaming half.</para>
    /// </summary>
    internal static class StableOwnershipPatches
    {
        public static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Stable), nameof(Stable.updateHorseOwnership)),
                prefix: new HarmonyMethod(typeof(StableOwnershipPatches), nameof(UpdateHorseOwnership_Prefix)));
        }

        private static bool UpdateHorseOwnership_Prefix(Stable __instance)
        {
            // Tractor garages and stables we don't manage keep vanilla behaviour entirely.
            if (__instance.IsTractorGarage() || !__instance.modData.ContainsKey(HorseHelper.CurrentFarmHorseIdKey))
                return true;

            if (__instance.isUnderConstruction())
                return false;

            Horse? horse = Utility.findHorse(__instance.HorseId);
            if (horse != null)
                horse.ownerId.Value = __instance.owner.Value;

            return false;
        }
    }
}
