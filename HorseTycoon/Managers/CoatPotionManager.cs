using HorseTycoon.Patches;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// The coat potion sold at Jadu's Summer Horse Festival stall (see data/ivpotions.json in the CP
    /// pack). Giving one to a horse re-rolls its coat to a different one of the seven in
    /// <see cref="HorseTexturePatches.AllSkinIds"/>. Purely cosmetic: no stats, tack, or sale value
    /// change. Applied from the same two interaction points as the other horse potions.
    /// </summary>
    public static class CoatPotionManager
    {
        public const string CoatPotionId = "HorseTycoon.CoatPotion";

        // Farmhand -> host: skinID is a net field on a host-owned FarmAnimal and
        // SyncStableHorseAppearance is host-only, so the host has to be the one to repaint the horse.
        // The roller sends the coat it picked so its own dialogue matches what everyone ends up seeing.
        private const string MsgCoatChange = "HorseTycoon.CoatChange";

        /// <param name="HorseId">The backing FarmAnimal's id (FarmAnimal.myID), stable across the network.</param>
        /// <param name="SkinId">The Data/FarmAnimals skin id the horse should take on.</param>
        private record CoatChangeMessage(long HorseId, string SkinId);

        private static IModHelper Helper = null!;

        public static void Initialize(IModHelper helper)
        {
            Helper = helper;
            helper.Events.Multiplayer.ModMessageReceived += OnMessageReceived;
        }

        public static bool IsCoatPotion(Item? item) => item?.ItemId == CoatPotionId;

        /// <summary>
        /// Gives the potion the farmer is holding to a horse, changing its coat. Returns true when the
        /// interaction was handled; false when the held item isn't a coat potion or the animal isn't a
        /// horse, so the caller falls through to vanilla behavior.
        /// </summary>
        public static bool TryApplyPotion(FarmAnimal animal, Farmer who)
        {
            if (!IsCoatPotion(who.CurrentItem) || !animal.type.Value.Contains("Horse"))
                return false;

            string oldSkinId = animal.skinID.Value ?? "";
            string newSkinId = RollNewCoat(oldSkinId);

            if (Context.IsMainPlayer)
                ApplyCoat(animal, newSkinId);
            else
                Helper.Multiplayer.SendMessage(
                    new CoatChangeMessage(animal.myID.Value, newSkinId),
                    MsgCoatChange,
                    modIDs: new[] { Helper.ModRegistry.ModID });

            string newCoatName = HorseTexturePatches.SkinNameFromId(newSkinId);
            who.reduceActiveItemByOne();
            animal.doEmote(20); // heart
            Game1.playSound("gulp");
            Game1.drawObjectDialogue($"{animal.displayName} drinks the potion... their coat shimmers and settles into a new one!");
            Logger.LogVerbose($"Coat potion applied to '{animal.displayName}' ({animal.myID.Value}): "
                + $"{HorseTexturePatches.SkinNameFromId(oldSkinId)} -> {newCoatName}.");
            return true;
        }

        /// <summary>Picks a coat at random from every coat except the one the horse is already wearing,
        /// so the potion always visibly changes something.</summary>
        private static string RollNewCoat(string currentSkinId)
        {
            string[] candidates = HorseTexturePatches.AllSkinIds
                .Where(id => id != currentSkinId)
                .ToArray();

            return candidates.Length > 0
                ? candidates[Game1.random.Next(candidates.Length)]
                : currentSkinId;
        }

        /// <summary>Writes the new coat to the persistent record and repaints the visible stable horse.
        /// Host only: both halves rely on netcode to reach the other players.</summary>
        private static void ApplyCoat(FarmAnimal animal, string newSkinId)
        {
            animal.skinID.Value = newSkinId;

            // Re-resolve the sprite sheet for the new skin, as HorseMarket does after a purchase.
            animal.reload(animal.home);

            // Rebuild the stable Horse character's appearance from the animal, preserving its tack.
            HorseHelper.SyncStableHorseAppearance();
        }

        private static void OnMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (!Context.IsMainPlayer || e.Type != MsgCoatChange || e.FromModID != Helper.ModRegistry.ModID)
                return;

            var msg = e.ReadAs<CoatChangeMessage>();
            FarmAnimal? animal = HorseHelper.GetHiddenHorseById(msg.HorseId);
            if (animal == null)
                return;

            ApplyCoat(animal, msg.SkinId);
            Logger.LogVerbose($"Applied farmhand coat change to '{animal.displayName}' ({msg.HorseId}): {msg.SkinId}.");
        }
    }
}
