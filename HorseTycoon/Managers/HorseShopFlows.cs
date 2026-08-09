using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// The confirm-and-buy half of the horse market, shared by the festival stalls
    /// (<see cref="FestivalRaceManager"/>) and the Town stall (<see cref="TownStallManager"/>).
    /// <para>Only the parts with real rules live here — the price/ownership checks and the calls into
    /// <see cref="HorseMarket"/>. Opening the menus is left to each caller, because the two settings
    /// differ: the festival keeper talks through a portrait dialogue and breeds the mares that were
    /// loaded onto the bus, while the Town vendor goes straight to the menu and breeds any mare in a
    /// barn at home.</para>
    /// </summary>
    internal static class HorseShopFlows
    {
        /// <summary>Asks the player to confirm buying <paramref name="offer"/>, then delivers it to a
        /// barn. Every exit path leads back into <paramref name="reopenMenu"/> so a declined or failed
        /// purchase doesn't kick the player out of the shop.</summary>
        public static void ConfirmPurchase(HorseOffer offer, Action reopenMenu)
        {
            Response[] yesNo =
            {
                new("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                new("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
            };
            Game1.currentLocation.createQuestionDialogue(
                $"Buy {offer.Name} for {Utility.getNumberWithCommas(offer.Price)}g?",
                yesNo,
                (_, answer) =>
                {
                    if (answer != "Yes")
                    {
                        Game1.afterDialogues = () => reopenMenu();
                        return;
                    }

                    // Another player may have bought this offer while the confirm dialogue was open.
                    if (offer.Purchased)
                    {
                        SayThenReturn("Sorry, that horse was just sold to someone else!", reopenMenu);
                        return;
                    }

                    if (Game1.player.Money < offer.Price)
                    {
                        SayThenReturn("You don't have enough gold.", reopenMenu);
                        return;
                    }
                    if (HorseHelper.GetAvailableBarn() == null)
                    {
                        SayThenReturn("You'll need a barn on your farm before I can deliver a horse.", reopenMenu);
                        return;
                    }
                    if (HorseHelper.GetBarnWithHorseSpace() == null)
                    {
                        SayThenReturn("Your barn is full! Make some room or build another stable and come see me again.", reopenMenu);
                        return;
                    }

                    HorseMarket.PurchaseHorse(offer);
                    SayThenReturn($"{offer.Name} has been delivered to your barn, ready and waiting for you at home!", reopenMenu);
                });
        }

        /// <summary>Asks the player to confirm hiring <paramref name="stud"/>, then has them pick which
        /// of their mares to breed. The fee is only charged once a mare is picked, so closing the
        /// picker cancels cleanly.</summary>
        /// <param name="getMares">The mares this shop will breed — the festival's are the ones brought
        /// on the bus, the Town vendor's are every eligible mare at home.</param>
        /// <param name="noMareLine">Shown when <paramref name="getMares"/> comes back empty.</param>
        public static void ConfirmStudService(HorseOffer stud, Func<List<FarmAnimal>> getMares,
            Action reopenMenu, string noMareLine)
        {
            Response[] yesNo =
            {
                new("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                new("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
            };
            Game1.currentLocation.createQuestionDialogue(
                $"Hire {stud.Name} for a {Utility.getNumberWithCommas(stud.Price)}g stud fee?",
                yesNo,
                (_, answer) =>
                {
                    if (answer != "Yes")
                    {
                        Game1.afterDialogues = () => reopenMenu();
                        return;
                    }

                    var mares = getMares();
                    if (mares.Count == 0)
                    {
                        Game1.afterDialogues = () => Game1.drawObjectDialogue(noMareLine);
                        return;
                    }

                    Game1.afterDialogues = () =>
                        Game1.activeClickableMenu = new Menus.HorseSelectMenu(
                            $"Breed with {stud.Name}",
                            mares,
                            mare =>
                            {
                                if (Game1.player.Money < stud.Price)
                                {
                                    Game1.drawObjectDialogue("You don't have enough gold.");
                                    Game1.afterDialogues = () => reopenMenu();
                                    return;
                                }
                                HorseMarket.PurchaseStudService(stud, mare);
                                Game1.drawObjectDialogue(
                                    $"{mare.Name} and {stud.Name} hit it off! {mare.Name} is now pregnant. " +
                                    $"The foal is due in {BreedingManager.GestationDays} days.");
                                Game1.afterDialogues = () => reopenMenu();
                            });
                });
        }

        /// <summary>Shows a message once the current dialogue closes, then reopens the shop menu.</summary>
        private static void SayThenReturn(string message, Action reopenMenu)
        {
            Game1.afterDialogues = () =>
            {
                Game1.drawObjectDialogue(message);
                Game1.afterDialogues = () => reopenMenu();
            };
        }
    }
}
