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
        private const string ItemShopActorName = "HorseTycoonItemShop";
        // Data/Shops key defined in the CP pack (data/ivpotions.json): Gold Carrot Seeds x2 per
        // player + exactly one of the three IV potions (synced daily pick), quantity 1 per player.
        private const string ItemShopId = "CP.HorseTycoon_IsaacFestivalShop";

        // Keeper intro lines are shown once per festival; later clicks go straight to the shop.
        private bool horseSellerIntroSeen;
        private bool studShopIntroSeen;

        /// <summary>Spawns the shop keeper event actors for the pasture phase. They're added to
        /// <see cref="spawnedSpectators"/> so the shops close (despawn) when the race starts.
        /// The sprite name is also the keeper's display name (Alesia/Isaac/Jadu); the character
        /// sheets and portraits always exist because the CP pack bundles them when SVE is absent.</summary>
        private void SpawnShopNpcs()
        {
            this.horseSellerIntroSeen = false;
            this.studShopIntroSeen = false;

            FestivalDefinition def = Def;
            this.SpawnShopNpc(def.HorseSellerTile, def.HorseSellerFacing, def.HorseSellerSprite, HorseSellerActorName, def.HorseSellerSprite);
            this.SpawnShopNpc(def.StudShopTile, def.StudShopFacing, def.StudShopSprite, StudShopActorName, def.StudShopSprite);
            this.SpawnShopNpc(def.ItemShopTile, def.ItemShopFacing, def.ItemShopSprite, ItemShopActorName, def.ItemShopSprite);
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

            // Portrait for the keeper's intro dialogue lines (vanilla dialogue box).
            try
            {
                actor.Portrait = Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>("Portraits\\" + spriteName);
            }
            catch
            {
                Logger.LogVerbose($"No portrait sheet found for festival shop NPC '{spriteName}'.");
            }

            RaceFestival.actors.Add(actor);
            spawnedSpectators.Add(actor);
            Logger.LogVerbose($"Spawned festival shop NPC '{actorName}' ({spriteName}) at {tile.Value}.");
        }

        /// <summary>Shows a normal NPC dialogue line (with portrait) for a stall keeper, optionally
        /// continuing into a follow-up action (e.g. opening the shop menu) when the box closes.</summary>
        private void Speak(NPC keeper, string text, Action? then = null)
        {
            keeper.CurrentDialogue.Clear();
            keeper.CurrentDialogue.Push(new Dialogue(keeper, null, text));
            Game1.drawDialogue(keeper);
            if (then != null)
                Game1.afterDialogues = () => then();
        }

        // ====================================================================================
        // Horse Seller
        // ====================================================================================

        private void OpenHorseSellerShop(NPC seller)
        {
            if (HorseMarket.GetSaleOffers().All(o => o.Purchased))
            {
                this.Speak(seller, "Sold out! Come back at the next festival.");
                return;
            }

            if (!this.horseSellerIntroSeen)
            {
                this.horseSellerIntroSeen = true;
                this.Speak(seller,
                    "Welcome! Finest horses this side of the valley — festival-grade, every one of 'em. Take a look!",
                    this.ShowHorseSaleMenu);
                return;
            }
            this.ShowHorseSaleMenu();
        }

        private void ShowHorseSaleMenu()
        {
            var offers = HorseMarket.GetSaleOffers();
            if (offers.All(o => o.Purchased))
            {
                Game1.drawObjectDialogue("Sold out! Come back at the next festival.");
                return;
            }
            Game1.activeClickableMenu = new HorseShopMenu("Horses for sale", offers, this.ConfirmHorsePurchase,
                Def.HorseSellerSprite, "Buy one of these beauties and I'll have it delivered straight to your farm!");
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
        // Item Shop (Isaac)
        // ====================================================================================

        private void OpenItemShop(NPC keeper)
        {
            // No intro — straight to shopping. The shop's Data/Shops Owners entry provides
            // Jadu's portrait and greeting inside the menu itself.
            if (!Utility.TryOpenShopMenu(ItemShopId, ownerName: null))
                Logger.LogVerbose($"Failed to open festival item shop '{ItemShopId}' — missing Data/Shops entry?");
        }

        // ====================================================================================
        // Stud Shop
        // ====================================================================================

        private void OpenStudShop(NPC studKeeper)
        {
            if (this.GetBroughtBreedableHorses().Count == 0)
            {
                this.Speak(studKeeper, "My stallions will need a mare that isn't already expecting, and it doesn't look like you brought one today. Bring one along next time!");
                return;
            }

            if (!this.studShopIntroSeen)
            {
                this.studShopIntroSeen = true;
                this.Speak(studKeeper,
                    "Looking to breed a champion? My stallions' fees are based on their pedigree — pick one and I'll introduce him to one of the horses you brought.",
                    this.ShowStudMenu);
                return;
            }
            this.ShowStudMenu();
        }

        private void ShowStudMenu()
        {
            Game1.activeClickableMenu = new HorseShopMenu("Stud services", HorseMarket.GetStudOffers(), this.ConfirmStudService,
                Def.StudShopSprite, "Take your pick — every one of my stallions is a proven runner.");
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
