using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Events;

namespace HorseTycoon.Patches
{
    /// <summary>
    /// Intercepts the vanilla overnight barn-birth event for Tycoon horses: instead of an
    /// instant foal, the mare becomes pregnant for <see cref="BreedingManager.GestationDays"/> days.
    /// </summary>
    public class PregnancyPatches
    {
        /// <summary>Birth events converted to pregnancies; their naming menu must be skipped.</summary>
        private static readonly HashSet<QuestionEvent> SuppressedBirthEvents = new();

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(QuestionEvent), nameof(QuestionEvent.setUp)),
                postfix: new HarmonyMethod(typeof(PregnancyPatches), nameof(QuestionEvent_setUp_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(QuestionEvent), nameof(QuestionEvent.tickUpdate)),
                prefix: new HarmonyMethod(typeof(PregnancyPatches), nameof(QuestionEvent_tickUpdate_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.CanHavePregnancy)),
                postfix: new HarmonyMethod(typeof(PregnancyPatches), nameof(CanHavePregnancy_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.getMoodMessage)),
                postfix: new HarmonyMethod(typeof(PregnancyPatches), nameof(GetMoodMessage_Postfix))
            );
        }

        /// <summary>
        /// The vanilla barn-birth event (whichQuestion == 2) sets its animal field and shows a
        /// "gave birth" dialogue when it proceeds. For horses, convert it to a pregnancy instead.
        /// </summary>
        private static void QuestionEvent_setUp_Postfix(QuestionEvent __instance, bool __result)
        {
            // __result == true means the event aborted; animal is only set for barn births.
            if (__result || __instance.animal == null || !__instance.animal.type.Value.Contains("Horse"))
                return;

            FarmAnimal mare = __instance.animal;
            FarmAnimal? sire = BreedingManager.MakePregnant(mare);

            // Replace the vanilla "gave birth" dialogue with a pregnancy announcement.
            Game1.activeClickableMenu = null;
            Game1.drawObjectDialogue(sire != null
                ? $"{mare.displayName} is pregnant with {sire.displayName}'s foal! It will arrive in {BreedingManager.GestationDays} days."
                : $"{mare.displayName} is pregnant! The foal will arrive in {BreedingManager.GestationDays} days.");

            SuppressedBirthEvents.Add(__instance);
        }

        /// <summary>Skips the vanilla foal-naming menu for births converted to pregnancies.</summary>
        private static bool QuestionEvent_tickUpdate_Prefix(QuestionEvent __instance, ref bool __result)
        {
            if (!SuppressedBirthEvents.Contains(__instance))
                return true;

            __result = __instance.forceProceed || !Game1.dialogueUp;
            if (__result)
                SuppressedBirthEvents.Remove(__instance);
            return false;
        }

        /// <summary>
        /// Horse pregnancies require a male/female pair: only females can get pregnant, and only
        /// when an adult male horse lives on the farm. Already-pregnant mares and horses hidden
        /// in a stable can't be picked either.
        /// </summary>
        private static void CanHavePregnancy_Postfix(FarmAnimal __instance, ref bool __result)
        {
            if (!__result || !__instance.type.Value.Contains("Horse"))
                return;

            if (__instance.isMale() ||
                HorseHelper.IsPregnant(__instance) ||
                HorseHelper.IsHidden(__instance) ||
                !BreedingManager.HasEligibleSire(__instance))
            {
                __result = false;
            }
        }

        /// <summary>Pregnancy flavor text in the animal status menu.</summary>
        private static void GetMoodMessage_Postfix(FarmAnimal __instance, ref string __result)
        {
            if (!HorseHelper.IsPregnant(__instance))
                return;

            int daysLeft = HorseHelper.GetPregnancyDaysLeft(__instance);
            __result = daysLeft switch
            {
                <= 1 => $"{__instance.displayName} is pregnant and could give birth any moment now!",
                2 => $"{__instance.displayName} is heavily pregnant. The foal is almost here...",
                _ => $"{__instance.displayName} is pregnant and resting quietly. The foal is due in {daysLeft} days.",
            };
        }
    }
}
