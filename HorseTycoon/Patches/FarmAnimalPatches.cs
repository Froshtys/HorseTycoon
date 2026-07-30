using HarmonyLib;
using HorseTycoon.Models;
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

            // --- Building Demolish Interceptor ---
            harmony.Patch(
                original: AccessTools.Method(typeof(Building), nameof(Building.BeforeDemolish)),
                prefix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(Building_BeforeDemolish_Prefix))
            );

            // --- Suppress vanilla horse naming for mod-managed stables ---
            harmony.Patch(
                original: AccessTools.Method(typeof(Horse), nameof(Horse.checkAction)),
                prefix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(Horse_checkAction_Prefix))
            );

            // --- IV potions: give a held potion to a barn horse instead of petting ---
            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.pet)),
                prefix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(Pet_Prefix))
            );

            // --- Horse sell price scales with IV/EV stats ---
            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.getSellPrice)),
                postfix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(GetSellPrice_Postfix))
            );

            // --- Stable/pen horses are fed from the silo instead of the barn trough ---
            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.dayUpdate)),
                prefix: new HarmonyMethod(typeof(FarmAnimalPatches), nameof(DayUpdate_Prefix))
            );
        }

        // --- Patch Implementations ---

        private static bool Draw_Prefix(FarmAnimal __instance)
        {
            return !HorseHelper.IsHidden(__instance);
        }

        private static bool Update_Prefix(FarmAnimal __instance, GameTime time, GameLocation location)
        {
            // Pregnant mares rest in place: skip AI so they don't wander or head outside.
            return !HorseHelper.IsHidden(__instance) && !HorseHelper.IsPregnant(__instance);
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
                            stable.modData[HorseHelper.StableEmptyKey] = "true";
                            Horse stableHorse = stable.getStableHorse();
                            if (stableHorse != null)
                            {
                                Game1.currentLocation?.characters.Remove(stableHorse);
                                stableHorse.currentLocation?.characters.Remove(stableHorse);
                            }
                            stable.HorseId = Guid.Empty;
                            // Stable stays theirs, but it's empty now, so their flute must report
                            // "no horse" rather than summoning the animal that was just sold.
                            StableOwnershipManager.SyncHorseNameForStableOwner(stable);
                            break;
                        }
                    }
                }
            }
        }

        public static bool Horse_checkAction_Prefix(Horse __instance)
        {
            // Breeding-pen proxy horses are decorative only, never mountable/interactable.
            if (__instance.modData.TryGetValue(HorseHelper.NoTackKey, out string? noTack) && noTack == "true")
                return false;

            Stable? stable = Game1.getFarm().buildings
                .OfType<Stable>()
                .FirstOrDefault(s => s.HorseId == __instance.HorseId);

            if (stable == null || !stable.modData.ContainsKey(HorseHelper.CurrentFarmHorseIdKey))
                return true;

            // Saddle equip: player holds a saddle item while not mounted.
            if (Game1.player.mount != __instance && HorseHelper.IsSaddleItem(Game1.player.ActiveItem))
            {
                string newSaddleId = Game1.player.ActiveItem.ItemId;
                string oldSaddleId = HorseHelper.GetEquippedSaddleId(__instance);

                // Return the old saddle to the player's inventory (drop on ground if full).
                Item oldSaddle = ItemRegistry.Create($"(O){oldSaddleId}");
                Item? overflow = Game1.player.addItemToInventory(oldSaddle);
                if (overflow != null)
                    Game1.createItemDebris(overflow, Game1.player.Position, -1);

                // Consume one from the held stack.
                Game1.player.reduceActiveItemByOne();

                HorseHelper.EquipSaddle(__instance, newSaddleId);
                Game1.playSound("dwop");
                return false;
            }

            // Potions and apple treats: player holds one while not mounted, so give it to the linked barn
            // animal. The heart emotes on the visible Horse, since the backing FarmAnimal is hidden.
            if (Game1.player.mount != __instance)
            {
                FarmAnimal? treatTarget = HorseHelper.GetFarmAnimalForHorse(__instance);
                if (treatTarget != null
                    && (TryApplyAnyPotion(treatTarget, Game1.player)
                        || AppleTreatManager.TryFeedApple(treatTarget, Game1.player, __instance)))
                    return false;
            }

            // On mount (not dismount), pet the horse if it hasn't been pet today.
            bool isMounting = Game1.player.mount != __instance;

            // Riding a stable horse while owning no stable of your own claims it, so the Horse Flute has
            // something to summon. Silent by design — the deliberate claim is the swap menu's button.
            // Checked before the petting block below, which returns early on the first interaction.
            if (isMounting
                && stable.owner.Value != Game1.player.UniqueMultiplayerID
                && StableOwnershipManager.GetOwnedStable(Game1.player.UniqueMultiplayerID) == null)
            {
                Logger.LogVerbose($"{Game1.player.Name} owns no stable; claiming '{__instance.Name}'s.");
                StableOwnershipManager.RequestStableClaim(stable, onlyIfUnowned: true);
            }

            if (isMounting)
            {
                FarmAnimal? animal = HorseHelper.GetFarmAnimalForHorse(__instance);
                if (animal != null && !animal.wasPet.Value)
                {
                    ApplyPetting(animal);
                    __instance.doEmote(20);
                    Game1.playSound("CP.HorseTycoon_Neigh");
                    return false; // don't mount yet; let the player interact again to ride
                }
            }

            return true;
        }

        /// <summary>
        /// Applies the friendship/happiness a real <see cref="FarmAnimal.pet"/> grants. Calling
        /// pet() directly isn't an option for a hidden horse: it would halt the player, turn them
        /// toward the animal's spot inside the barn, and refuse outright after 7pm.
        /// </summary>
        private static void ApplyPetting(FarmAnimal animal)
        {
            animal.wasPet.Value = true;
            ApplyPettingBonus(animal);
        }

        /// <summary>
        /// The friendship and happiness half of a petting, without marking the animal as pet for the day.
        /// Apple treats grant exactly this, so a treat is worth one extra petting on top of the daily one.
        /// </summary>
        public static void ApplyPettingBonus(FarmAnimal animal)
        {
            animal.friendshipTowardFarmer.Value = Math.Min(1000, animal.friendshipTowardFarmer.Value + 15);
            int happinessDrain = animal.GetAnimalData()?.HappinessDrain ?? 0;
            animal.happiness.Value = (byte)Math.Min(255, animal.happiness.Value + Math.Max(5, 30 + happinessDrain));
        }

        /// <summary>
        /// Feeds hidden horses (stable-active or penned) straight from the silo.
        /// A barn's auto-feeder only fills one trough tile per <see cref="AnimalHouse.animalLimit"/>,
        /// and there are exactly that many trough tiles, so the extra horses our stable capacity
        /// bonus allows would find no hay and go hungry forever. A hidden horse isn't standing in
        /// the barn anyway, so it takes its hay from the silo and leaves the troughs to the rest.
        /// </summary>
        public static void DayUpdate_Prefix(FarmAnimal __instance)
        {
            if (__instance.type.Value?.Contains("Horse") != true || !HorseHelper.IsHidden(__instance))
                return;
            if (__instance.fullness.Value >= 200)
                return;

            // Falls through to the vanilla trough logic when the silo is empty.
            GameLocation root = (__instance.homeInterior ?? Game1.getFarm()).GetRootLocation();
            if (GameLocation.GetHayFromAnySilo(root) != null)
                __instance.fullness.Value = 255;
        }

        /// <summary>Adds 500g per 10 combined IV/EV points across all stats to a horse's sell price.</summary>
        public static void GetSellPrice_Postfix(FarmAnimal __instance, ref int __result)
        {
            if (__instance.type.Value == null || !__instance.type.Value.Contains("Horse"))
                return;

            HorseStats stats = new(__instance);
            int totalPoints = stats.TotalSpeed + stats.TotalSprint + stats.TotalJump;
            __result += totalPoints / 10 * 500;
        }

        /// <summary>Clicking a barn horse while holding a potion or an apple gives it that instead of petting.</summary>
        public static bool Pet_Prefix(FarmAnimal __instance, Farmer who, bool is_auto_pet)
        {
            if (is_auto_pet || who == null)
                return true;
            return !TryApplyAnyPotion(__instance, who) && !AppleTreatManager.TryFeedApple(__instance, who);
        }

        /// <summary>
        /// Offers the farmer's held item to each horse potion in turn. Every TryApplyPotion returns
        /// false for an item it doesn't own, so at most one of them acts. Returns true when the click
        /// was consumed by a potion and the caller should skip its vanilla behavior.
        /// </summary>
        private static bool TryApplyAnyPotion(FarmAnimal animal, Farmer who) =>
            IVPotionManager.TryApplyPotion(animal, who)
            || TrainingPotionManager.TryApplyPotion(animal, who)
            || CoatPotionManager.TryApplyPotion(animal, who);

        public static void Building_BeforeDemolish_Prefix(Building __instance)
        {
            // Free any horses penned in a breeding pen before it's demolished.
            if (BreedingPenManager.IsBreedingPen(__instance))
            {
                BreedingPenManager.ReleasePennedHorses(__instance);
                return;
            }

            if (__instance is Stable stable)
            {
                if (stable.modData.TryGetValue(HorseHelper.CurrentFarmHorseIdKey, out string linkedIdString) &&
                    long.TryParse(linkedIdString, out long targetAnimalId))
                {
                    FarmAnimal? hiddenHorse = HorseHelper.GetHiddenHorseById(targetAnimalId);

                    if (hiddenHorse != null)
                    {
                        HorseHelper.RestoreHorse(hiddenHorse);
                    }
                }
            }
        }
    }
}