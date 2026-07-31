using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;

namespace HorseTycoon
{
    /// <summary>
    /// The travelling desert merchant's caravan, borrowed from the vanilla Calico Desert. The wagon and
    /// the trader are map-less sprites there (<see cref="StardewValley.Locations.Desert"/> adds them to
    /// <c>temporarySprites</c> and fakes the collision with a hard-coded bounds rectangle), so the same
    /// art is rebuilt here relative to <see cref="FestivalDefinition.DesertTraderTile"/>: sprites for the
    /// look, invisible <see cref="CaravanBlocker"/> objects for the footprint, and the two counter tiles
    /// at the front open his trade.
    /// <para>He doesn't bring his usual desert wares to the festival; he has exactly one thing to sell,
    /// and it depends on the player's mods. With SVE installed that's a camel (SVE's farm animal) for
    /// a Gold Horseshoe, and he haggles down to three Iron Horseshoes if the first price is refused.
    /// (Horseshoes only drop from the player's own horses, and the gold ones need 800 friendship, so
    /// even these prices are a season or two of care — see data/horses.json.)
    /// Without SVE he offers one mystery saddle a day, haggling the same way.</para>
    /// Everything is local to each client (like the starting stalls, none of it is net-synced), and it
    /// is torn down when the race starts, alongside the other stalls.
    /// </summary>
    public partial class FestivalRaceManager
    {
        /// <summary>SVE's content pack, which defines the camel (Data/FarmAnimals) and its art.</summary>
        private const string SveModId = "FlashShifter.StardewValleyExpandedCP";
        private const string CamelAnimalId = "FlashShifter.StardewValleyExpandedCP_Camel";

        // The merchant's two asking prices for a camel, and what the saddle costs when there's no camel
        // to sell. Both horseshoes are the mod's own items (CP pack, data/items.json).
        private const string GoldHorseshoeItemId = "(O)HorseTycoon.ShoeGold";
        private const string IronHorseshoeItemId = "(O)HorseTycoon.ShoeIron";
        private const int CamelPriceInGoldShoes = 1;
        private const int CamelPriceInIronShoes = 3;
        private const int SaddlePriceInGoldShoes = 1;
        private const int SaddlePriceInIronShoes = 3;

        /// <summary>The saddles the merchant's mystery saddle can turn out to be. Deliberately an
        /// allow-list rather than "everything except X": the pride flag saddles and the Rainbow one
        /// aren't random-loot material, the Ice one is excluded, the gradient ones stay festival-shop
        /// exclusives, and Brown is what every horse already wears by default
        /// (<see cref="HorseHelper.DefaultSaddleId"/>).</summary>
        private static readonly string[] TraderSaddleIds =
        {
            "HorseTycoon.SaddleWhite",
            "HorseTycoon.SaddleBlack",
            "HorseTycoon.SaddleRed",
            "HorseTycoon.SaddleOrange",
            "HorseTycoon.SaddleTeal",
            "HorseTycoon.SaddleLavender",
            "HorseTycoon.SaddleNavy",
            "HorseTycoon.SaddlePink",
            "HorseTycoon.SaddleGold",
            "HorseTycoon.SaddlePeach",
            "HorseTycoon.SaddlePlum",
            "HorseTycoon.SaddleSky",
            "HorseTycoon.SaddleMint",
        };

        /// <summary>Whether the local player has already bought the merchant's one saddle today.
        /// Declining doesn't count: he keeps the same saddle on the table until it's sold.</summary>
        private bool traderSaddleSold;

        // Source rects on LooseSprites\temporary_sprites_1 and their offsets (in world pixels) from the
        // top-left corner of DesertTraderTile, all copied from Desert.resetLocalState so the caravan
        // looks exactly like it does in the desert. Vanilla anchors it at tile (33, 18): the wagon is
        // drawn at (528, 298) * 4 = (2112, 1192) world px, i.e. 40px below that tile's top edge.
        private static readonly Rectangle CaravanSource = new(0, 513, 208, 101);
        private static readonly Vector2 CaravanOffset = new(0f, 40f);
        private static readonly Rectangle TraderSource = new(0, 614, 20, 26);
        private static readonly Vector2 TraderOffset = new(540f, 264f);

        // Vanilla draws the wagon at a fixed layer depth of 0.1324 (and the trader just in front at
        // 0.1328) rather than sorting by its own bottom edge, which puts the sort line partway down the
        // wagon so players walking past the front are drawn over it. Those constants are world-Y 1324/1328
        // against a wagon top of 1192, i.e. 132/136px below it.
        private const float CaravanDepthOffset = 132f;
        private const float TraderDepthOffset = 136f;

        // Collision footprint: vanilla's desertMerchantBounds is (2112, 1280, 836, 280): a 14x5 tile block
        // starting two rows below the anchor. Its right column and bottom row are trimmed here, since the
        // rectangle rounds up past the wagon art on both edges and would otherwise block bare sand.
        private const int CaravanBlockWidth = 13;
        private const int CaravanBlockHeight = 4;
        private const int CaravanBlockTopOffset = 2;

        // The counter tiles the trader serves from. Vanilla's Desert.checkAction listens on (41-42, 24),
        // the bottom row of the footprint under the trader sprite. The customer stands on the row below and
        // reaches over, so this follows the footprint whenever its height changes.
        private const int TraderCounterXOffset = 8;
        private const int TraderCounterYOffset = CaravanBlockTopOffset + CaravanBlockHeight - 1;

        private readonly PerScreen<List<TemporaryAnimatedSprite>> caravanSprites = new(() => new List<TemporaryAnimatedSprite>());
        private readonly PerScreen<List<Vector2>> caravanBlockerTiles = new(() => new List<Vector2>());

        /// <summary>Parks the merchant's caravan for the pasture phase (no-op for festivals whose
        /// definition has no <see cref="FestivalDefinition.DesertTraderTile"/>).</summary>
        private void SpawnDesertTrader()
        {
            GameLocation loc = Game1.currentLocation;
            Point? anchor = Def.DesertTraderTile;
            if (loc == null || anchor == null || this.caravanSprites.Value.Count > 0)
                return;

            this.traderSaddleSold = false;

            Vector2 anchorPixels = new Vector2(anchor.Value.X, anchor.Value.Y) * 64f;
            Texture2D sheet = Game1.temporaryContent.Load<Texture2D>("LooseSprites\\temporary_sprites_1");

            this.AddCaravanSprite(loc, sheet, CaravanSource, anchorPixels + CaravanOffset,
                (anchorPixels.Y + CaravanOffset.Y + CaravanDepthOffset) / 10000f);
            this.AddCaravanSprite(loc, sheet, TraderSource, anchorPixels + TraderOffset,
                (anchorPixels.Y + CaravanOffset.Y + TraderDepthOffset) / 10000f);

            for (int dx = 0; dx < CaravanBlockWidth; dx++)
            {
                for (int dy = 0; dy < CaravanBlockHeight; dy++)
                {
                    var tile = new Vector2(anchor.Value.X + dx, anchor.Value.Y + CaravanBlockTopOffset + dy);
                    if (loc.objects.ContainsKey(tile))
                        continue;
                    loc.objects[tile] = new CaravanBlocker(tile);
                    this.caravanBlockerTiles.Value.Add(tile);
                }
            }

            Logger.LogVerbose($"Parked the desert trader's caravan at {anchor.Value}.");
        }

        private void AddCaravanSprite(GameLocation loc, Texture2D sheet, Rectangle source, Vector2 position, float layerDepth)
        {
            var sprite = new TemporaryAnimatedSprite
            {
                texture = sheet,
                sourceRect = source,
                sourceRectStartingPos = new Vector2(source.X, source.Y),
                animationLength = 1,
                totalNumberOfLoops = 9999,
                interval = 99999f,
                scale = 4f,
                position = position,
                layerDepth = layerDepth,
            };
            loc.temporarySprites.Add(sprite);
            this.caravanSprites.Value.Add(sprite);
        }

        /// <summary>Packs the caravan away again (race start, or festival cleanup). Safe to call twice.</summary>
        private void RemoveDesertTrader()
        {
            GameLocation? loc = Game1.currentLocation;
            if (loc != null)
            {
                foreach (TemporaryAnimatedSprite sprite in this.caravanSprites.Value)
                    loc.temporarySprites.Remove(sprite);
                foreach (Vector2 tile in this.caravanBlockerTiles.Value)
                    if (loc.objects.TryGetValue(tile, out var obj) && obj is CaravanBlocker)
                        loc.objects.Remove(tile);
            }
            this.caravanSprites.Value.Clear();
            this.caravanBlockerTiles.Value.Clear();
        }

        /// <summary>Whether the player is standing in front of the caravan's counter and facing it, the
        /// same way vanilla's Desert.checkAction gates the trader's two tiles.</summary>
        private bool IsFacingDesertTrader()
        {
            // Sprites exist only while a caravan is parked, so this also covers "no festival active".
            if (this.caravanSprites.Value.Count == 0)
                return false;
            Point? anchor = Def.DesertTraderTile;
            if (anchor == null)
                return false;

            Vector2 tile = Game1.player.Tile;
            Vector2 facing = Game1.player.FacingDirection switch
            {
                Game1.up => new Vector2(0f, -1f),
                Game1.right => new Vector2(1f, 0f),
                Game1.down => new Vector2(0f, 1f),
                _ => new Vector2(-1f, 0f),
            };
            Vector2 target = tile + facing;

            int counterY = anchor.Value.Y + TraderCounterYOffset;
            int counterX = anchor.Value.X + TraderCounterXOffset;
            return target.Y == counterY && (target.X == counterX || target.X == counterX + 1);
        }

        /// <summary>Talks to the merchant. He has one thing to sell: a camel if SVE is installed,
        /// otherwise a mystery saddle.</summary>
        private void OpenDesertTraderShop()
        {
            // No gold changes hands here (he only takes horseshoes), so the money dial is left alone.
            if (this.SellsCamel)
                this.OfferCamel();
            else
                this.OfferSaddle();
        }

        /// <summary>The camel is SVE's farm animal, so it's only for sale when SVE is installed.</summary>
        private bool SellsCamel => this.Helper.ModRegistry.IsLoaded(SveModId);

        // ====================================================================================
        // Camel (SVE installed)
        // ====================================================================================

        /// <summary>Opens with the gold-horseshoe price; a refusal gets the iron-horseshoe counter-offer.</summary>
        private void OfferCamel()
        {
            this.AskForCamel(
                $"Fancy a well bred Camel? She's a fine gal. How about for {Shoes(CamelPriceInGoldShoes, "gold")}.",
                GoldHorseshoeItemId, CamelPriceInGoldShoes,
                declined: () => this.AskForCamel(
                    $"Fine, fine... how about {Shoes(CamelPriceInIronShoes, "iron")} instead?",
                    IronHorseshoeItemId, CamelPriceInIronShoes,
                    declined: () => Game1.drawObjectDialogue("Your loss. The camel stays with me.")));
        }

        /// <summary>"1 gold horseshoe" / "3 iron horseshoes", so the asking prices read naturally
        /// whatever the constants above are set to.</summary>
        private static string Shoes(int count, string metal) =>
            $"{count} {metal} horseshoe{(count == 1 ? "" : "s")}";

        /// <summary>One camel asking price: confirm, take the horseshoes, deliver the animal.</summary>
        private void AskForCamel(string question, string priceItemId, int priceAmount, Action declined)
        {
            Response[] yesNo =
            {
                new("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                new("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
            };
            Game1.currentLocation.createQuestionDialogue(question, yesNo, (_, answer) =>
            {
                if (answer != "Yes")
                {
                    Game1.afterDialogues = () => declined();
                    return;
                }

                if (!Game1.player.Items.ContainsId(priceItemId, priceAmount))
                {
                    Game1.afterDialogues = () => Game1.drawObjectDialogue(
                        $"The merchant counts your horseshoes and shakes their head. Come back when you have {priceAmount} horse shoes.");
                    return;
                }

                // Built up front so CanLiveIn picks the housing the same way Marnie's shop does.
                var camel = new FarmAnimal(CamelAnimalId, Game1.Multiplayer.getNewID(), Game1.player.UniqueMultiplayerID);
                Building? home = FindHomeFor(camel);
                if (home == null)
                {
                    Game1.afterDialogues = () => Game1.drawObjectDialogue(
                        "A camel needs a roof of its own. Come back when you have room in a barn.");
                    return;
                }

                Game1.afterDialogues = () => this.NameAndDeliverCamel(camel, home, priceItemId, priceAmount);
            });
        }

        private void NameAndDeliverCamel(FarmAnimal camel, Building home, string priceItemId, int priceAmount)
        {
            Game1.activeClickableMenu = new NamingMenu(
                name =>
                {
                    // Re-checked here because the horseshoes are only taken once the naming is done.
                    if (Game1.player.Items.ReduceId(priceItemId, priceAmount) < priceAmount)
                    {
                        Game1.exitActiveMenu();
                        Game1.drawObjectDialogue("You no longer have enough horseshoes.");
                        return;
                    }

                    camel.Name = name;
                    camel.displayName = name;
                    ((AnimalHouse)home.GetIndoors()).adoptAnimal(camel);

                    Game1.exitActiveMenu();
                    Game1.playSound("purchase");
                    Game1.drawObjectDialogue($"{name} has been sent along to your farm. Take good care of them!");
                    Logger.LogVerbose($"Bought camel '{name}' from the festival desert trader for {priceAmount}x {priceItemId}; housed in {home.buildingType.Value}.");
                },
                title: "Name your new camel:",
                defaultName: "Callie");
        }

        // ====================================================================================
        // Mystery saddle (no SVE)
        // ====================================================================================

        /// <summary>One saddle a day, sight unseen, with the same gold-then-iron haggle as the camel.
        /// Declining costs nothing — the same saddle is still under the counter next time the player
        /// comes by — but once it's bought he's done for the day.</summary>
        private void OfferSaddle()
        {
            if (this.traderSaddleSold)
            {
                Game1.drawObjectDialogue("That was the only one I had, friend. Try me at the next festival.");
                return;
            }

            this.AskForSaddle(
                $"Tack, hand-made, great leather. I won't say the color. {Shoes(SaddlePriceInGoldShoes, "gold")}.",
                GoldHorseshoeItemId, SaddlePriceInGoldShoes,
                declined: () => this.AskForSaddle(
                    $"Fine, fine... how about {Shoes(SaddlePriceInIronShoes, "iron")} instead?",
                    IronHorseshoeItemId, SaddlePriceInIronShoes,
                    declined: () => Game1.drawObjectDialogue("Your loss. It stays under my counter.")));
        }

        /// <summary>One saddle asking price: confirm, take the horseshoes, hand over the mystery tack.</summary>
        private void AskForSaddle(string question, string priceItemId, int priceAmount, Action declined)
        {
            Response[] yesNo =
            {
                new("Yes", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_Yes")),
                new("No", Game1.content.LoadString("Strings\\Lexicon:QuestionDialogue_No")),
            };
            Game1.currentLocation.createQuestionDialogue(question, yesNo, (_, answer) =>
            {
                if (answer != "Yes")
                {
                    Game1.afterDialogues = () => declined();
                    return;
                }

                if (!Game1.player.Items.ContainsId(priceItemId, priceAmount))
                {
                    Game1.afterDialogues = () => Game1.drawObjectDialogue(
                        $"The merchant looks over your bag. Come back when you have {priceAmount} horse shoes.");
                    return;
                }

                Game1.player.Items.ReduceId(priceItemId, priceAmount);
                this.traderSaddleSold = true;

                Item saddle = ItemRegistry.Create($"(O){this.PickTraderSaddleId()}");
                Game1.playSound("purchase");
                Logger.LogVerbose($"Bought mystery saddle '{saddle.ItemId}' from the festival desert trader for {priceAmount}x {priceItemId}.");
                Game1.afterDialogues = () =>
                {
                    Game1.drawObjectDialogue($"You unwrap the tack: {saddle.DisplayName}!");
                    Game1.player.addItemByMenuIfNecessary(saddle);
                };
            });
        }

        /// <summary>Which saddle today's wrapped bundle turns out to hold. Seeded per day and save so
        /// every player at the festival is offered the same one, and it changes at the next festival.</summary>
        private string PickTraderSaddleId()
        {
            System.Random rng = Utility.CreateDaySaveRandom(7841.0);
            return TraderSaddleIds[rng.Next(TraderSaddleIds.Length)];
        }

        /// <summary>The first building on the farm the animal can live in that isn't full.</summary>
        private static Building? FindHomeFor(FarmAnimal animal)
        {
            return Game1.getFarm().buildings.FirstOrDefault(b =>
                animal.CanLiveIn(b) && b.GetIndoors() is AnimalHouse house && !house.isFull());
        }
    }

    /// <summary>
    /// An invisible, indestructible object used only to give the desert trader's caravan a collision
    /// footprint. Festival collision (Event.checkForCollision) tests object bounding boxes, so a plain
    /// object in the tile is enough to stop players walking through the wagon; everything that would
    /// otherwise draw it, break it, or let a tool hit it is overridden away.
    /// </summary>
    public class CaravanBlocker : StardewValley.Object
    {
        public CaravanBlocker() : base(Vector2.Zero, "390") { }

        public CaravanBlocker(Vector2 tile) : base(tile, "390") { }

        public override bool isPassable() => false;

        public override bool performToolAction(Tool t) => false;

        public override bool checkForAction(Farmer who, bool justCheckingForActivity = false) => false;

        public override void draw(SpriteBatch spriteBatch, int x, int y, float alpha = 1f) { }

        public override void draw(SpriteBatch spriteBatch, int xNonTile, int yNonTile, float layerDepth, float alpha = 1f) { }

        public override void drawWhenHeld(SpriteBatch spriteBatch, Vector2 objectPosition, Farmer f) { }
    }
}
