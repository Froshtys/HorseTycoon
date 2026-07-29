using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// The training potion sold at Jadu's Summer Horse Festival stall (see data/ivpotions.json in the
    /// CP pack). Giving one to a horse halves that horse's daily training requirement in all three
    /// stats for the rest of the day, so a single day's riding goes twice as far. Applied from the
    /// same two interaction points as the IV potions: clicking a barn horse (FarmAnimal.pet prefix)
    /// and clicking a managed stable horse while dismounted (Horse.checkAction prefix).
    /// </summary>
    public static class TrainingPotionManager
    {
        public const string TrainingPotionId = "HorseTycoon.TrainingPotion";

        public static bool IsTrainingPotion(Item? item) => item?.ItemId == TrainingPotionId;

        /// <summary>
        /// Gives the potion the farmer is holding to a horse. Returns true when the interaction was
        /// handled (including the "already boosted today" case, which shows a message without consuming
        /// the potion); false when the held item isn't a training potion or the animal isn't a horse,
        /// so the caller falls through to vanilla behavior.
        /// </summary>
        public static bool TryApplyPotion(FarmAnimal animal, Farmer who)
        {
            if (!IsTrainingPotion(who.CurrentItem) || !animal.type.Value.Contains("Horse"))
                return false;

            if (TrainingManager.HasTrainingBoost(animal))
            {
                Game1.drawObjectDialogue($"{animal.displayName} is already fired up for today's training.");
                return true;
            }

            TrainingManager.GrantTrainingBoost(animal);
            who.reduceActiveItemByOne();
            animal.doEmote(20); // heart
            Game1.playSound("gulp");
            Game1.drawObjectDialogue($"{animal.displayName} drinks the potion... today's training will come twice as easily!");
            Logger.LogVerbose($"Training potion applied to '{animal.displayName}' ({animal.myID.Value}): daily requirements halved for today.");
            return true;
        }
    }
}
