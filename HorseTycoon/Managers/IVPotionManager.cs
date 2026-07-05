using HorseTycoon.Models;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// The IV potions sold at Isaac's Summer Horse Festival stall (see data/ivpotions.json in the
    /// CP pack). Giving a potion to one of your horses raises the matching stat's IV by one grade
    /// (+10, IVs move in 10-point steps up to <see cref="HorseStats.IV_MAX"/>). Application is
    /// routed from two interaction points: clicking a barn horse (FarmAnimal.pet prefix) and
    /// clicking a managed stable horse while dismounted (Horse.checkAction prefix).
    /// </summary>
    public static class IVPotionManager
    {
        public const string SpeedPotionId = "HorseTycoon.IVPotionSpeed";
        public const string SprintPotionId = "HorseTycoon.IVPotionSprint";
        public const string JumpPotionId = "HorseTycoon.IVPotionJump";

        /// <summary>The stat a potion item improves ("Speed"/"Sprint"/"Jump"), or null if the item isn't an IV potion.</summary>
        public static string? GetPotionStat(string? itemId) => itemId switch
        {
            SpeedPotionId => "Speed",
            SprintPotionId => "Sprint",
            JumpPotionId => "Jump",
            _ => null,
        };

        public static bool IsIVPotion(Item? item) => item != null && GetPotionStat(item.ItemId) != null;

        /// <summary>
        /// Gives the potion the farmer is holding to a horse: +1 IV grade in the potion's stat.
        /// Returns true when the interaction was handled (including the "already maxed" case, which
        /// shows a message without consuming the potion); false when the held item isn't a potion
        /// or the animal isn't a horse, so the caller falls through to vanilla behavior.
        /// </summary>
        public static bool TryApplyPotion(FarmAnimal animal, Farmer who)
        {
            string? stat = GetPotionStat(who.CurrentItem?.ItemId);
            if (stat == null || !animal.type.Value.Contains("Horse"))
                return false;

            HorseStats stats = animal.GetHorseStats();
            int current = stat switch
            {
                "Speed" => stats.SpeedIV,
                "Sprint" => stats.SprintIV,
                _ => stats.JumpIV,
            };

            if (current >= HorseStats.IV_MAX)
            {
                Game1.drawObjectDialogue($"{animal.displayName}'s natural {stat.ToLower()} is already at its peak — the potion would be wasted.");
                return true;
            }

            switch (stat)
            {
                case "Speed": stats.SpeedIV = current + 10; break;
                case "Sprint": stats.SprintIV = current + 10; break;
                default: stats.JumpIV = current + 10; break;
            }

            who.reduceActiveItemByOne();
            animal.doEmote(20); // heart
            Game1.playSound("gulp");
            Game1.drawObjectDialogue($"{animal.displayName} drinks the potion... {stat} improved!");
            Logger.LogVerbose($"IV potion applied to '{animal.displayName}' ({animal.myID.Value}): {stat} IV {current} -> {current + 10}.");
            return true;
        }
    }
}
