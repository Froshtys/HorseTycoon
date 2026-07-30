using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// Apple treats. Handing a horse an apple grants exactly what a petting does, on top of the day's
    /// petting — but only twice per week per horse; after that the horse has had its fill and the apple
    /// stays in the player's inventory. Offered from both horse-interaction points, so it works whether
    /// the horse is a visible barn animal (FarmAnimal.pet prefix) or a managed stable horse the player
    /// isn't riding (Horse.checkAction prefix).
    /// </summary>
    public static class AppleTreatManager
    {
        /// <summary>Vanilla Apple (forage/fruit tree object 613).</summary>
        public const string AppleQualifiedId = "(O)613";

        /// <summary>Treats allowed per horse per week.</summary>
        public const int TreatsPerWeek = 2;

        private const string TreatWeekKey = "Froshty.HorseTycoon/AppleTreatWeek";
        private const string TreatCountKey = "Froshty.HorseTycoon/AppleTreatCount";

        public static bool IsApple(Item? item) => item?.QualifiedItemId == AppleQualifiedId;

        /// <summary>Weeks since the save began. Stardew's calendar starts on a Monday, so this rolls over
        /// with the in-game week rather than a floating 7-day window.</summary>
        private static int CurrentWeek => Game1.Date.TotalDays / 7;

        /// <summary>How many treats this horse has already had this week.</summary>
        public static int TreatsThisWeek(FarmAnimal horse)
        {
            if (!horse.modData.TryGetValue(TreatWeekKey, out string week)
                || !int.TryParse(week, out int weekNumber)
                || weekNumber != CurrentWeek)
                return 0;

            return horse.modData.TryGetValue(TreatCountKey, out string count) && int.TryParse(count, out int treats)
                ? treats
                : 0;
        }

        public static bool CanTreat(FarmAnimal horse) => TreatsThisWeek(horse) < TreatsPerWeek;

        /// <summary>
        /// Feeds the apple the farmer is holding to a horse. Returns true when the interaction was handled
        /// (including the "already had its treats" case, which shows a message without consuming the apple);
        /// false when the held item isn't an apple or the animal isn't a horse, so the caller falls through
        /// to vanilla behavior.
        /// </summary>
        /// <param name="emoteOn">Who shows the heart. Pass the visible <see cref="StardewValley.Characters.Horse"/>
        /// for a hidden horse, whose backing FarmAnimal isn't on screen to emote.</param>
        public static bool TryFeedApple(FarmAnimal animal, Farmer who, Character? emoteOn = null)
        {
            if (!IsApple(who.CurrentItem) || animal.type.Value?.Contains("Horse") != true)
                return false;

            int alreadyFed = TreatsThisWeek(animal);
            if (alreadyFed >= TreatsPerWeek)
            {
                Game1.drawObjectDialogue($"{animal.displayName} has already had {(TreatsPerWeek == 2 ? "both" : "all")} of this week's apples.");
                return true;
            }

            animal.modData[TreatWeekKey] = CurrentWeek.ToString();
            animal.modData[TreatCountKey] = (alreadyFed + 1).ToString();

            // Worth exactly one petting, and it doesn't set wasPet — so an apple is a second petting for the
            // day rather than a replacement for it.
            FarmAnimalPatches.ApplyPettingBonus(animal);

            who.reduceActiveItemByOne();
            (emoteOn ?? animal).doEmote(20); // heart
            Game1.playSound("eat");

            int treatsLeft = TreatsPerWeek - (alreadyFed + 1);
            Game1.drawObjectDialogue(treatsLeft > 0
                ? $"{animal.displayName} crunches the apple happily."
                : $"{animal.displayName} crunches the apple happily... and looks like it could go for another next week.");

            Logger.LogVerbose($"Apple treat given to '{animal.displayName}' ({animal.myID.Value}): " +
                $"friendship={animal.friendshipTowardFarmer.Value}, happiness={animal.happiness.Value}, " +
                $"treat {alreadyFed + 1}/{TreatsPerWeek} of week {CurrentWeek}.");
            return true;
        }
    }
}
