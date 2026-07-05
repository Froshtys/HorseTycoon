using HorseTycoon.Models;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace HorseTycoon
{
    /// <summary>
    /// Horse pregnancy lifecycle: replaces the instant vanilla barn birth with a 7-day
    /// gestation. The pregnant mare rests at the bottom-left of her home barn until the
    /// foal arrives.
    /// </summary>
    public static class BreedingManager
    {
        public const int GestationDays = 7;

        /// <summary>Marks a mare as pregnant and sends her to the birthing area.</summary>
        public static void MakePregnant(FarmAnimal mare)
        {
            mare.modData[HorseHelper.PregnancyDaysLeftKey] = GestationDays.ToString();
            SendToBirthingArea(mare);
            Logger.LogVerbose($"{mare.Name} ({mare.myID.Value}) is now pregnant, due in {GestationDays} days.");
        }

        /// <summary>Host-only daily tick: advance every pregnancy, delivering foals that are due.</summary>
        public static void OnDayStarted()
        {
            List<FarmAnimal> dueMares = new();

            foreach (FarmAnimal mare in HorseHelper.GetAllBarnHorses())
            {
                if (!HorseHelper.IsPregnant(mare))
                    continue;

                int daysLeft = HorseHelper.GetPregnancyDaysLeft(mare) - 1;
                if (daysLeft > 0)
                {
                    mare.modData[HorseHelper.PregnancyDaysLeftKey] = daysLeft.ToString();
                    SendToBirthingArea(mare);
                    Logger.LogVerbose($"{mare.Name} is pregnant: {daysLeft} day(s) until birth.");
                }
                else
                {
                    dueMares.Add(mare);
                }
            }

            DeliverFoals(dueMares, 0);
        }

        /// <summary>Parks the mare at the bottom-left corner of her home barn and stops her wandering.</summary>
        public static void SendToBirthingArea(FarmAnimal mare)
        {
            if (mare.home?.GetIndoors() is not AnimalHouse interior)
                return;

            // Make sure the mare is registered indoors (mirrors HorseHelper.RestoreHorse).
            if (mare.currentLocation != interior)
            {
                mare.currentLocation?.animals.Remove(mare.myID.Value);
                if (!interior.animals.ContainsKey(mare.myID.Value))
                    interior.animals.Add(mare.myID.Value, mare);
                mare.currentLocation = interior;
            }

            int mapHeight = interior.map.Layers[0].LayerHeight;
            Vector2 restTile = Utility.recursiveFindOpenTileForCharacter(mare, interior, new Vector2(2, mapHeight - 4), 12);
            mare.Position = restTile * 64f;
            mare.Halt();
            mare.FacingDirection = 2;
            mare.controller = null;
        }

        /// <summary>Delivers foals one at a time so each gets its own naming menu.</summary>
        private static void DeliverFoals(List<FarmAnimal> dueMares, int index)
        {
            if (index >= dueMares.Count)
                return;

            FarmAnimal mare = dueMares[index];
            if (mare.home?.GetIndoors() is not AnimalHouse interior)
            {
                // Home barn is gone — postpone one day rather than losing the foal.
                mare.modData[HorseHelper.PregnancyDaysLeftKey] = "1";
                DeliverFoals(dueMares, index + 1);
                return;
            }

            mare.modData.Remove(HorseHelper.PregnancyDaysLeftKey);

            Game1.activeClickableMenu = new NamingMenu(
                name =>
                {
                    FarmAnimal foal = new FarmAnimal("Tycoon.Horse", Game1.Multiplayer.getNewID(), Game1.player.UniqueMultiplayerID);
                    foal.Name = name;
                    foal.displayName = name;
                    foal.parentId.Value = mare.myID.Value;
                    foal.GetHorseStats().RandomizeStats(HorseStats.HorseSourceQuality.Starter);
                    interior.adoptAnimal(foal);
                    foal.Position = mare.Position + new Vector2(64f, 0f);

                    Logger.LogVerbose($"{mare.Name} gave birth to foal '{name}' ({foal.myID.Value}).");
                    Game1.exitActiveMenu();
                    DeliverFoals(dueMares, index + 1);
                },
                title: $"{mare.displayName} gave birth! Name the foal:"
            );
        }
    }
}
