using Microsoft.Xna.Framework;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// Festival market stalls: the Horse Seller (buy a generated Special-quality horse, delivered to
    /// a barn at home) and the Stud Shop (pay a stud fee, then pick one of the horses you brought to
    /// breed with the stallion). Both are event actors spawned during the pasture phase on festivals
    /// whose <see cref="FestivalDefinition"/> sets the shop tiles (currently the summer bus festival);
    /// clicks route here from <c>OnButtonPressed</c>. Offer generation, pricing, and the
    /// host-authoritative delivery live in <see cref="HorseMarket"/>.
    /// </summary>
    public partial class FestivalRaceManager
    {
        private const string HorseSellerActorName = "HorseTycoonHorseSeller";
        private const string StudShopActorName = "HorseTycoonStudShop";

        /// <summary>Spawns the shop keeper event actors for the pasture phase. They're added to
        /// <see cref="spawnedSpectators"/> so the shops close (despawn) when the race starts.</summary>
        private void SpawnShopNpcs()
        {
            FestivalDefinition def = Def;
            this.SpawnShopNpc(def.HorseSellerTile, def.HorseSellerFacing, def.HorseSellerSprite, HorseSellerActorName, "Horse Trader");
            this.SpawnShopNpc(def.StudShopTile, def.StudShopFacing, def.StudShopSprite, StudShopActorName, "Stud Master");
        }

        private void SpawnShopNpc(Point? tile, int facing, string spriteName, string actorName, string displayName)
        {
            if (tile == null || RaceFestival == null)
                return;

            // Fresh event actor (same approach as SpawnSpectators) with a unique actor name so
            // getActorByName can't collide with real NPCs; only the sprite sheet is borrowed.
            var sprite = new AnimatedSprite("Characters\\" + spriteName, 0, 16, 32);
            var actor = new NPC(sprite, TileToPixels(tile.Value), facing, actorName);
            actor.displayName = displayName;
            actor.faceDirection(facing);
            actor.Sprite.StopAnimation();
            actor.EventActor = true;
            RaceFestival.actors.Add(actor);
            spawnedSpectators.Add(actor);
            Logger.LogVerbose($"Spawned festival shop NPC '{actorName}' ({spriteName}) at {tile.Value}.");
        }

        // ====================================================================================
        // Horse Seller
        // ====================================================================================

        private void OpenHorseSellerShop(NPC seller)
        {
            Response[] options =
            {
                new("Browse", "Show me the horses"),
                new("No", "Not right now"),
            };
            Game1.currentLocation.createQuestionDialogue(
                "Welcome! Finest horses this side of the valley — festival-grade, every one of 'em. Care to take a look?",
                options,
                (_, answer) =>
                {
                    if (answer != "Browse")
                        return;
                    Game1.afterDialogues = this.ShowHorseSaleMenu;
                }, seller);
        }

        private void ShowHorseSaleMenu()
        {
            var offers = HorseMarket.GetSaleOffers();
            if (offers.All(o => o.Purchased))
            {
                Game1.drawObjectDialogue("Sold out! Come back at the next festival.");
                return;
            }
            Game1.activeClickableMenu = new HorseShopMenu("Horses for sale", offers, this.ConfirmHorsePurchase);
        }

        private void ConfirmHorsePurchase(HorseOffer offer)
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
                        Game1.afterDialogues = this.ShowHorseSaleMenu;
                        return;
                    }

                    // Another player may have bought this offer while the confirm dialogue was open.
                    if (offer.Purchased)
                    {
                        Game1.afterDialogues = () => Game1.drawObjectDialogue("Sorry — that horse was just sold to someone else!");
                        return;
                    }

                    if (Game1.player.Money < offer.Price)
                    {
                        Game1.afterDialogues = () => Game1.drawObjectDialogue("You don't have enough gold.");
                        return;
                    }
                    if (HorseHelper.GetAvailableBarn() == null)
                    {
                        Game1.afterDialogues = () => Game1.drawObjectDialogue("You'll need a barn on your farm before I can deliver a horse.");
                        return;
                    }
                    if (HorseHelper.GetBarnWithHorseSpace() == null)
                    {
                        Game1.afterDialogues = () => Game1.drawObjectDialogue("Your barn is full! Make some room — or build another stable — and come see me again.");
                        return;
                    }

                    HorseMarket.PurchaseHorse(offer);
                    Game1.afterDialogues = () => Game1.drawObjectDialogue($"{offer.Name} has been delivered to your barn, ready and waiting for you at home!");
                });
        }

        // ====================================================================================
        // Stud Shop
        // ====================================================================================

        private void OpenStudShop(NPC studKeeper)
        {
            Response[] options =
            {
                new("Browse", "Show me the studs"),
                new("No", "Not right now"),
            };
            Game1.currentLocation.createQuestionDialogue(
                "Looking to breed a champion? My stallions' fees are based on their pedigree — pick one and I'll introduce him to one of the horses you brought.",
                options,
                (_, answer) =>
                {
                    if (answer != "Browse")
                        return;

                    if (this.GetBroughtBreedableHorses().Count == 0)
                    {
                        Game1.afterDialogues = () => Game1.drawObjectDialogue("My stallions will need a mare that isn't already expecting, and it doesn't look like you brought one today. Bring one along next time!");
                        return;
                    }
                    Game1.afterDialogues = this.ShowStudMenu;
                }, studKeeper);
        }

        private void ShowStudMenu()
        {
            Game1.activeClickableMenu = new HorseShopMenu("Stud services", HorseMarket.GetStudOffers(), this.ConfirmStudService);
        }

        private void ConfirmStudService(HorseOffer stud)
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
                        Game1.afterDialogues = this.ShowStudMenu;
                        return;
                    }

                    var mares = this.GetBroughtBreedableHorses();
                    if (mares.Count == 0)
                    {
                        Game1.afterDialogues = () => Game1.drawObjectDialogue("My stallions will need a mare that isn't already expecting, and it doesn't look like you brought one today.");
                        return;
                    }

                    // Fee is charged when the mare is picked, so closing the picker cancels cleanly.
                    Game1.afterDialogues = () =>
                        Game1.activeClickableMenu = new HorseSelectMenu(
                            $"Breed with {stud.Name}",
                            mares,
                            mare =>
                            {
                                if (Game1.player.Money < stud.Price)
                                {
                                    Game1.drawObjectDialogue("You don't have enough gold.");
                                    return;
                                }
                                HorseMarket.PurchaseStudService(stud, mare);
                                Game1.drawObjectDialogue(
                                    $"{mare.Name} and {stud.Name} hit it off! {mare.Name} is now pregnant — " +
                                    $"the foal is due in {BreedingManager.GestationDays} days.");
                            });
                });
        }

        /// <summary>The local player's horses at this festival that can be bred with a stud: the mares
        /// loaded onto the bus — stallions, babies, and already-pregnant mares are excluded (same sex
        /// rules as barn breeding, see <see cref="BreedingManager.GetEligibleSires"/>).</summary>
        private List<FarmAnimal> GetBroughtBreedableHorses()
        {
            var result = new List<FarmAnimal>();
            var barnHorses = HorseHelper.GetAllBarnHorses();
            foreach (long animalId in SummerBusHorseIds)
            {
                FarmAnimal? animal = barnHorses.FirstOrDefault(a => a.myID.Value == animalId);
                if (animal != null && !animal.isMale() && !animal.isBaby() && !HorseHelper.IsPregnant(animal))
                    result.Add(animal);
            }
            return result;
        }
    }
}
