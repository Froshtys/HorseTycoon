using HorseTycoon.Menus;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// The two horse vendors' shops at the Town stall. Same offers, prices and menus as the summer
    /// festival's stalls (<see cref="HorseMarket"/> generates one list a day for both), with the
    /// checkout logic shared through <see cref="HorseShopFlows"/>.
    /// <para>The differences from the festival are deliberate: there's no keeper NPC to talk through
    /// here (the vendor is a temporary sprite, not an actor), so clicking the counter goes straight to
    /// the menu; and the Stud Master breeds any eligible mare in a barn at home, since nobody had to
    /// load a horse onto a bus to reach Pelican Town.</para>
    /// </summary>
    public static partial class TownStallManager
    {
        /// <summary>Player stallions already sold to the Stud Master today (one sale per horse per
        /// visit); shown greyed out in the offer menu. Cleared at day start.</summary>
        private static readonly HashSet<long> soldStudIds = new();

        /// <summary>How much of the day's list a vendor brings to town. They travel light — the point
        /// is that the festival is where you go for the full choice (6 for sale, 4 at stud).
        /// <para>Taken off the front of the list rather than filtered by what's still available, so the
        /// three on offer in town are a fixed subset of the day's horses: sell one and two are left,
        /// rather than a fourth stepping up to replace it.</para></summary>
        private const int TownOfferLimit = 3;

        /// <summary>Today's offers, as this vendor brings them to town.</summary>
        internal static List<HorseOffer> GetTownOffers(TownVendor vendor)
            => (vendor == TownVendor.StudMaster ? HorseMarket.GetStudOffers() : HorseMarket.GetSaleOffers())
                .Take(TownOfferLimit)
                .ToList();

        private const string AllStudsHiredLine =
            "That's every one of my stallions spoken for by you today. Come find me next time!";

        /// <summary>Entry point from the counter's tile action.</summary>
        private static void OpenVendorShop(TownVendor vendor)
        {
            switch (vendor)
            {
                case TownVendor.HorseSeller:
                    ShowSaleMenu();
                    break;

                case TownVendor.StudMaster:
                    ShowStudChoices();
                    break;

                default:
                    // Jadu's counter keeps the map's own OpenShop action, so this shouldn't be
                    // reachable — but a stale action left on a tile shouldn't do nothing at all.
                    if (!Utility.TryOpenShopMenu(ShopId, ownerName: null))
                        Logger.LogVerbose($"Failed to open Town stall shop '{ShopId}'. Missing Data/Shops entry?");
                    break;
            }
        }

        // ====================================================================================
        // Horse Seller
        // ====================================================================================

        private static void ShowSaleMenu()
        {
            var offers = GetTownOffers(TownVendor.HorseSeller);
            if (!offers.Any(o => o.IsAvailable))
            {
                Game1.drawObjectDialogue("Sold out! I'll bring more next time I'm through town.");
                return;
            }

            Game1.activeClickableMenu = new HorseShopMenu("Horses for sale", offers,
                offer => HorseShopFlows.ConfirmPurchase(offer, ShowSaleMenu),
                HorseSellerSprite,
                "Buy one of these beauties and I'll have it delivered straight to your farm!");
        }

        // ====================================================================================
        // Stud Master
        // ====================================================================================

        private static void ShowStudChoices()
        {
            Response[] choices =
            {
                new("Browse", "Browse studs"),
                new("Offer", "Offer my studs"),
                new("Leave", "Leave"),
            };

            Game1.currentLocation.createQuestionDialogue("What would you like to do?", choices, (_, answer) =>
            {
                switch (answer)
                {
                    case "Browse":
                        if (GetBreedableMares().Count == 0)
                        {
                            Game1.afterDialogues = () => Game1.drawObjectDialogue(NoMareLine);
                            return;
                        }
                        if (!GetTownOffers(TownVendor.StudMaster).Any(o => o.IsAvailable))
                        {
                            Game1.afterDialogues = () => Game1.drawObjectDialogue(AllStudsHiredLine);
                            return;
                        }
                        Game1.afterDialogues = ShowStudMenu;
                        break;

                    case "Offer":
                        var studs = GetOfferableStuds();
                        if (studs.Count == 0)
                        {
                            Game1.afterDialogues = () => Game1.drawObjectDialogue(
                                "I'd pay well for a good stallion's services, but it doesn't look like you own one.");
                            return;
                        }
                        Game1.afterDialogues = () =>
                            Game1.activeClickableMenu = new StudOfferMenu(studs, soldStudIds, StudMasterSprite);
                        break;
                }
            });
        }

        private static void ShowStudMenu()
        {
            var studs = GetTownOffers(TownVendor.StudMaster);
            if (!studs.Any(o => o.IsAvailable))
            {
                Game1.drawObjectDialogue(AllStudsHiredLine);
                return;
            }

            Game1.activeClickableMenu = new HorseShopMenu("Stud services", studs,
                stud => HorseShopFlows.ConfirmStudService(stud, GetBreedableMares, ShowStudMenu, NoMareLine),
                StudMasterSprite,
                "Take your pick. Every one of my stallions is a proven champion.");
        }

        private const string NoMareLine =
            "You haven't got a mare ready to breed. Come back when one's grown and not already in foal.";

        /// <summary>The player's mares that can be bred: grown, not male, not already pregnant. Unlike
        /// the festival's list these don't have to have been brought anywhere — the mare stays home and
        /// the visit is arranged at the stall.</summary>
        private static List<FarmAnimal> GetBreedableMares()
            => HorseHelper.GetAllBarnHorses()
                .Where(a => !a.isMale() && !a.isBaby() && !HorseHelper.IsPregnant(a))
                .ToList();

        /// <summary>The player's stallions whose services can be sold: grown males from any barn.
        /// Horses already sold today stay in the list so the menu can grey them out.</summary>
        private static List<FarmAnimal> GetOfferableStuds()
            => HorseHelper.GetAllBarnHorses()
                .Where(a => a.isMale() && !a.isBaby())
                .ToList();
    }
}
