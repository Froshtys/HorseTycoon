using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// The travelling desert merchant's caravan, borrowed from the vanilla Calico Desert. The wagon and
    /// the trader are map-less sprites there (<see cref="StardewValley.Locations.Desert"/> adds them to
    /// <c>temporarySprites</c> and fakes the collision with a hard-coded bounds rectangle), so the same
    /// art is rebuilt here relative to <see cref="FestivalDefinition.DesertTraderTile"/>: sprites for the
    /// look, invisible <see cref="CaravanBlocker"/> objects for the footprint, and the two counter tiles
    /// at the front open the vanilla "DesertTrade" shop (trade Omni Geodes etc. for desert goods).
    /// Everything is local to each client (like the starting stalls, none of it is net-synced), and it
    /// is torn down when the race starts, alongside the other stalls.
    /// </summary>
    public partial class FestivalRaceManager
    {
        /// <summary>Vanilla's Data/Shops key for the desert trader (Game1.shop_desertTrader).</summary>
        private const string DesertTradeShopId = "DesertTrade";

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

        private void OpenDesertTraderShop()
        {
            SnapMoneyDial();
            if (!Utility.TryOpenShopMenu(DesertTradeShopId, Game1.currentLocation))
                Logger.LogVerbose($"Failed to open the desert trader shop '{DesertTradeShopId}'.");
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
