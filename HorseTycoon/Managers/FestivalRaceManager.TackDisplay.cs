using System;
using System.Collections.Generic;
using System.Linq;
using HorseTycoon.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.GameData.Shops;

namespace HorseTycoon
{
    /// <summary>
    /// The decorative tack at Leah's festival stall — three saddles laid out on her table and two
    /// mannequins standing behind it. None of it is authored on the map: it's read back out of her
    /// Data/Shops entry so the stall always advertises what she's actually selling that year.
    /// <para>Her stock rotates by year (the HorseTycoon_YEAR_MOD conditions in the CP pack's
    /// data/festival.json), and duplicating those rules here would mean two places to keep in step,
    /// so the shop data stays the single source of truth: we evaluate the same conditions the shop
    /// menu does and display whatever survives.</para>
    /// <para>The mannequins wear the priciest tack — the year's rare rotating saddles — and the
    /// table takes the rest. The saddles are a tile swap (per-colour sprites already live on
    /// HorseTycoonTileSet), but the mannequins are drawn from the same overlay sheets the horses
    /// and the mannequin furniture use, so any colour works without new art.</para>
    /// </summary>
    public partial class FestivalRaceManager
    {
        private const string TackSheetId = "HorseTycoonTileSet";
        private const int TackSheetColumns = 28;
        private const string SaddleIdPrefix = "HorseTycoon.Saddle";

        /// <summary>
        /// Tack colours in the order their cells sit on HorseTycoonTileSet.png, keyed by the top row
        /// of each row pair. Cell k occupies (row, 2k) = shop icon, (row, 2k+1) = saddle,
        /// (row+1, 2k) = bridle.
        /// <para>MUST MATCH the ROWS dict in tools/build_tack_tiles.py — that script paints the very
        /// cells this table indexes into, so a colour added to one has to be added to the other and
        /// the sheet rebuilt.</para>
        /// </summary>
        private static readonly Dictionary<int, string[]> TackSheetRows = new()
        {
            [0] = new[] { "Ace", "Bisexual", "Black", "Brown", "Ice", "Lavender", "Lesbian",
                          "NonBinary", "Orange", "Rainbow", "Red", "Teal", "Trans", "White" },
            [5] = new[] { "Aurora", "Candy", "Gold", "Green", "Meadow", "Navy", "Ocean",
                          "Peach", "Pink", "Plum", "Sunset", "Sky", "Ember", "Mint" },
        };

        /// <summary>Colour name → local tile index of that colour's saddle sprite on the sheet.</summary>
        private static readonly Dictionary<string, int> SaddleTileByColour = BuildSaddleTileIndex();

        private static Dictionary<string, int> BuildSaddleTileIndex()
        {
            var map = new Dictionary<string, int>();
            foreach ((int row, string[] colours) in TackSheetRows)
                for (int k = 0; k < colours.Length; k++)
                    map[colours[k]] = row * TackSheetColumns + (2 * k + 1); // saddle sits at col 2k+1
            return map;
        }

        /// <summary>Local index of every saddle sprite on the sheet, for spotting a display slot.
        /// A slot is MARKED BY a painted saddle: paint any colour where you want one and it gets
        /// repointed at the year's stock. Remove it and that slot simply stops existing.</summary>
        private static readonly HashSet<int> SaddleCells = new(SaddleTileByColour.Values);

        /// <summary>Layers a saddle slot may be painted on, so it doesn't matter which front layer
        /// the stall's shelf art happens to live on.</summary>
        private static readonly string[] SaddleSlotLayers = { "Front", "Front2", "AlwaysFront", "AlwaysFront2" };

        // Mannequins live on rows 3-4 of the sheet as 2x2 blocks, two columns each. Everything left
        // of MannequinFirstColumn on those rows is other scenery (the SHOP sign at cols 0-1, the
        // horse statue at cols 2-3), so that's what separates a mannequin from the rest of the row;
        // new variants get added rightward and are picked up automatically.
        private const int MannequinRow = 3;
        private const int MannequinFirstColumn = 4;

        /// <summary>Top-left column of each mannequin variant → the bare (untacked) variant of the
        /// same kind. Cloth mannequins sit at col 4 and its tacked variants; wood at col 6 and its
        /// own. Used to strip authored tack without turning a cloth stand into a wooden one.</summary>
        private static readonly Dictionary<int, int> BareMannequinColumn = new()
        {
            [4] = 4,   // cloth, plain
            [12] = 4,  // cloth + Plum
            [14] = 4,  // cloth + Peach
            [6] = 6,   // wood, plain
            [8] = 6,   // wood + Orange
            [10] = 6,  // wood + Teal
        };

        /// <summary>Side-on tack frame the mannequin pose was traced from: row 1, frame 0 of the
        /// shared horse overlay sheet. The same frame <see cref="MannequinPatches"/> draws on the
        /// mannequin furniture, so map and furniture wear identical pixels.</summary>
        private static readonly Rectangle MannequinTackFrame = new(0, 32, 32, 32);

        /// <summary>Sprite-pixel nudge that lands that frame on the tilesheet's mannequin art. The
        /// furniture uses (-1,-1); the tilesheet's copy of the same art sits one pixel further right,
        /// so the x nudge cancels out.</summary>
        private static readonly Point MannequinTackOffset = new(0, -1);

        /// <summary>Authored tile indices for every display slot, captured before we overwrite them.
        /// The festival Map comes out of the shared xTile content cache, so the same instance is
        /// handed back on every entry and our writes persist — without this, a slot we skip in some
        /// later year would silently keep the previous year's sprite.</summary>
        private readonly Dictionary<(string Layer, Point Tile), int> tackDisplayOriginals = new();

        /// <summary>The tack sprites we added to the location. PerScreen because split-screen players
        /// each have their own currentLocation and temporarySprites list.</summary>
        private readonly PerScreen<List<TemporaryAnimatedSprite>> tackDisplaySprites =
            new(() => new List<TemporaryAnimatedSprite>());

        /// <summary>Display slots found on the current festival map, refreshed on every entry.
        /// Instance fields rather than locals so a re-entry reuses the lists.</summary>
        private readonly List<(string Layer, Point Tile)> detectedSaddleTiles = new();
        private readonly List<Point> detectedMannequinTiles = new();

        /// <summary>One saddle colour the shop is actually offering, for display ranking.</summary>
        private readonly record struct StockedTack(string Colour, int Price, string ItemId);

        /// <summary>
        /// The saddle colours whose shop conditions pass right now, most expensive first. Fully
        /// deterministic — the only inputs are Data/Shops and the date — so every client and every
        /// re-entry produces the same order.
        /// </summary>
        private static List<StockedTack> ReadStockedTack(string shopId)
        {
            var stock = new List<StockedTack>();

            Dictionary<string, ShopData> shops;
            try
            {
                shops = DataLoader.Shops(Game1.content);
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"Tack display: couldn't load Data/Shops ({ex.Message}).");
                return stock;
            }

            if (!shops.TryGetValue(shopId, out ShopData? shop) || shop?.Items == null)
            {
                Logger.LogVerbose($"Tack display: no Data/Shops entry '{shopId}'.");
                return stock;
            }

            const string qualifiedPrefix = "(O)" + SaddleIdPrefix;
            foreach (ShopItemData item in shop.Items)
            {
                // Skip anything that isn't tack — the stall also sells the mannequin furniture.
                if (item?.ItemId == null || !item.ItemId.StartsWith(qualifiedPrefix, StringComparison.Ordinal))
                    continue;

                string colour = item.ItemId[qualifiedPrefix.Length..];
                if (colour.Length == 0)
                    continue;

                // The same check the shop menu makes; a null/blank condition means always stocked.
                // Evaluated for the LOCAL player on purpose: today's conditions are all year-based so
                // every client agrees, and if a player-scoped one is ever added the display should
                // follow it, because then it still matches what that player sees in the shop menu.
                if (!string.IsNullOrWhiteSpace(item.Condition) && !GameStateQuery.CheckConditions(item.Condition))
                    continue;

                stock.Add(new StockedTack(colour, item.Price, item.ItemId));
            }

            return stock
                .OrderByDescending(t => t.Price)
                .ThenBy(t => t.ItemId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Finds the display slots by reading the map, so the stall can be moved, resized or
        /// rearranged entirely in Tiled without touching code — the same "the painted art IS the
        /// data" approach <see cref="ReadNpcPlacements"/> and the going/jump-zone loaders use.
        /// <para>A saddle slot is any Front2 tile showing one of the sheet's saddle sprites; a
        /// mannequin is any Front tile showing the top-left cell of a mannequin block. Both are
        /// returned in scan order, so slots fill left to right across a row.</para>
        /// </summary>
        private static void DetectTackDisplayTiles(GameLocation loc, List<(string Layer, Point Tile)> saddleTiles, List<Point> mannequinTiles)
        {
            saddleTiles.Clear();
            mannequinTiles.Clear();

            foreach (string layerName in SaddleSlotLayers)
            {
                foreach (Point tile in Scan(layerName, index => SaddleCells.Contains(index)))
                    saddleTiles.Add((layerName, tile));
            }

            // Mannequins are only recognised on "Front": the block's top half has to be on that
            // layer (and its bottom half on "Buildings") for the tack to sort correctly.
            mannequinTiles.AddRange(Scan("Front", IsMannequinTopLeft));

            List<Point> Scan(string layerName, Func<int, bool> match)
            {
                var found = new List<Point>();
                var layer = loc.map?.GetLayer(layerName);
                if (layer == null)
                    return found;

                for (int y = 0; y < layer.LayerHeight; y++)
                {
                    for (int x = 0; x < layer.LayerWidth; x++)
                    {
                        var tile = layer.Tiles[x, y];
                        if (tile?.TileSheet?.Id == TackSheetId && match(tile.TileIndex))
                            found.Add(new Point(x, y));
                    }
                }
                return found;
            }
        }

        /// <summary>Whether a tile index is the top-left cell of a mannequin block. Blocks are two
        /// columns wide, so only even columns qualify — that skips each block's own right half.</summary>
        private static bool IsMannequinTopLeft(int tileIndex)
        {
            int row = tileIndex / TackSheetColumns;
            int col = tileIndex % TackSheetColumns;
            return row == MannequinRow && col >= MannequinFirstColumn && col % 2 == 0;
        }

        /// <summary>
        /// Points the stall's saddles and mannequins at this year's stock. Called once per festival
        /// entry from <see cref="EnterPasture"/>, after the temporary map is live.
        /// </summary>
        private void ApplyTackDisplay()
        {
            FestivalDefinition def = Def;
            if (string.IsNullOrEmpty(def.TackDisplayShopId))
                return;

            GameLocation? loc = Game1.currentLocation;
            if (loc?.map == null)
                return;

            // Always start from the authored art, so a slot we don't fill this year can't keep
            // whatever was put there last time — and so detection below reads the map as authored.
            this.RestoreTackDisplay();
            this.ClearTackDisplaySprites();

            DetectTackDisplayTiles(loc, this.detectedSaddleTiles, this.detectedMannequinTiles);

            // Explicit coordinates on the definition win, for a stall whose layout the scan can't
            // express; otherwise whatever was painted on the map is the layout.
            IReadOnlyList<(string Layer, Point Tile)> saddleSlots = def.TackDisplaySaddleTiles.Length > 0
                ? def.TackDisplaySaddleTiles.Select(t => ("Front2", t)).ToList()
                : this.detectedSaddleTiles;
            IReadOnlyList<Point> mannequinSlots = def.TackDisplayMannequinTiles.Length > 0
                ? def.TackDisplayMannequinTiles
                : this.detectedMannequinTiles;

            Logger.LogVerbose($"Tack display: {mannequinSlots.Count} mannequin(s), {saddleSlots.Count} saddle slot(s).");

            List<StockedTack> ranked = ReadStockedTack(def.TackDisplayShopId!);
            int next = 0;

            // Mannequins first: they take the priciest tack, and they can wear any colour that has
            // overlay art, including ones with no cell on the map tilesheet.
            foreach (Point tile in mannequinSlots)
            {
                string? colour = null;
                while (next < ranked.Count)
                {
                    string candidate = ranked[next++].Colour;
                    if (HorseTexturePatches.GetOverlayTexture($"Saddle_{candidate}") != null)
                    {
                        colour = candidate;
                        break;
                    }
                    Logger.LogVerbose($"Tack display: no overlay art for '{candidate}'; skipping it on the mannequins.");
                }

                this.StripMannequinTack(loc, tile);
                if (colour != null)
                    this.DrawMannequinTack(loc, tile, colour);
            }

            // Then the saddle slots, in scan order, with whatever's left that has a sprite on the sheet.
            foreach ((string layerName, Point tile) in saddleSlots)
            {
                int index = -1;
                while (next < ranked.Count)
                {
                    string candidate = ranked[next++].Colour;
                    if (SaddleTileByColour.TryGetValue(candidate, out index))
                        break;
                    Logger.LogVerbose($"Tack display: '{candidate}' has no cell on {TackSheetId}; skipping it on the table.");
                    index = -1;
                }

                // Nothing left to show: RestoreTackDisplay already put the authored saddle back.
                if (index >= 0)
                    this.SetTackTile(layerName, tile, index);
            }
        }

        /// <summary>
        /// Swaps a mannequin to the untacked version of whatever kind it already is, so the year's
        /// tack can be drawn on a clean stand. Keeps cloth cloth and wood wood — the map author
        /// picks the stand, we only supply the tack.
        /// </summary>
        private void StripMannequinTack(GameLocation loc, Point topLeftTile)
        {
            var authored = loc.map?.GetLayer("Front")?.Tiles[topLeftTile.X, topLeftTile.Y];
            if (authored?.TileSheet?.Id != TackSheetId)
            {
                Logger.LogVerbose($"Tack display: Front({topLeftTile.X},{topLeftTile.Y}) isn't a mannequin; skipping.");
                return;
            }

            int col = authored.TileIndex % TackSheetColumns;
            if (!BareMannequinColumn.TryGetValue(col, out int bareCol))
            {
                // An unrecognised variant: leave the art alone and just wear the tack over it.
                Logger.LogVerbose($"Tack display: mannequin cell at column {col} has no known bare version; leaving it as authored.");
                return;
            }

            this.SetTackTile("Front", topLeftTile, MannequinRow * TackSheetColumns + bareCol);
            this.SetTackTile("Front", new Point(topLeftTile.X + 1, topLeftTile.Y),
                MannequinRow * TackSheetColumns + bareCol + 1);
            this.SetTackTile("Buildings", new Point(topLeftTile.X, topLeftTile.Y + 1),
                (MannequinRow + 1) * TackSheetColumns + bareCol);
            this.SetTackTile("Buildings", new Point(topLeftTile.X + 1, topLeftTile.Y + 1),
                (MannequinRow + 1) * TackSheetColumns + bareCol + 1);
        }

        /// <summary>Repoints one map tile at a different sprite on the same tilesheet, remembering
        /// what was there first. Fails soft if the map isn't laid out the way we expect.</summary>
        private void SetTackTile(string layerName, Point tile, int tileIndex)
        {
            var existing = Game1.currentLocation?.map?.GetLayer(layerName)?.Tiles[tile.X, tile.Y];
            if (existing == null || existing.TileSheet?.Id != TackSheetId)
            {
                Logger.LogVerbose($"Tack display: {layerName}({tile.X},{tile.Y}) isn't a {TackSheetId} tile; skipping.");
                return;
            }

            this.tackDisplayOriginals.TryAdd((layerName, tile), existing.TileIndex);
            existing.TileIndex = tileIndex;
        }

        /// <summary>
        /// Puts every tile we've touched back to its authored sprite.
        /// <para>Only forgets the tiles it actually restored. Reset() can run once the player has
        /// already warped off the festival map, and dropping the record there would strand the
        /// cached map mid-swap: the next entry would then capture this year's display as the
        /// "authored" art and never be able to get back to the real thing.</para>
        /// </summary>
        private void RestoreTackDisplay()
        {
            var restored = new List<(string Layer, Point Tile)>();

            foreach (((string layerName, Point tile), int index) in this.tackDisplayOriginals)
            {
                var existing = Game1.currentLocation?.map?.GetLayer(layerName)?.Tiles[tile.X, tile.Y];
                // Guarded in case the map asset was reloaded underneath us, so a stale index can't
                // get written over unrelated art.
                if (existing == null || existing.TileSheet?.Id != TackSheetId)
                    continue;

                existing.TileIndex = index;
                restored.Add((layerName, tile));
            }

            foreach ((string Layer, Point Tile) key in restored)
                this.tackDisplayOriginals.Remove(key);
        }

        /// <summary>
        /// Draws a saddle and bridle onto a bare map mannequin.
        /// <para>These go on the location as temporary sprites rather than being drawn in
        /// RenderedWorld, for the same reason the parked bus does (see <see cref="ParkBus"/>):
        /// RenderedWorld paints over the player. Temporary sprites are drawn inside the world's
        /// sorted batch, so they sort against characters and map layers properly.</para>
        /// </summary>
        private void DrawMannequinTack(GameLocation loc, Point topLeftTile, string colour)
        {
            Vector2 origin = new Vector2(topLeftTile.X * 64f, topLeftTile.Y * 64f)
                             + new Vector2(MannequinTackOffset.X * 4f, MannequinTackOffset.Y * 4f);

            string[] overlayNames = { $"Saddle_{colour}", $"Bridle_{colour}" };
            for (int i = 0; i < overlayNames.Length; i++)
            {
                Texture2D? texture = HorseTexturePatches.GetOverlayTexture(overlayNames[i]);
                if (texture == null)
                    continue;

                // Every sprite gets its OWN depth. Equal layerDepths are a tie, and the sorted batch
                // resolves ties arbitrarily and re-resolves them as the sprite list shifts, so a
                // shared depth makes the tack flicker in and out from behind the mannequin.
                float nudge = TackDepthNudge * (i + 1);

                // Split in two so each half sorts like the mannequin half it sits on: the top half
                // is a "Front" tile (drawn over the player), the bottom half a "Buildings" tile
                // (drawn under). Anything else and the saddle would clip through the player.
                //
                // The cut is at the DESTINATION tile boundary, not the frame's midpoint. The tack is
                // nudged up a pixel, so the boundary falls a row later in the source; splitting at
                // the midpoint instead pushed the bottom half's first row up into the Front tile's
                // row, where the mannequin's own art (far higher depth) drew straight over it and
                // ate a pixel row out of the saddle and bridle.
                int splitRow = MannequinTackFrame.Height / 2 - MannequinTackOffset.Y;

                this.AddTackSprite(loc, texture,
                    new Rectangle(MannequinTackFrame.X, MannequinTackFrame.Y, MannequinTackFrame.Width, splitRow),
                    origin, FrontLayerDepth(topLeftTile.Y) + nudge);

                this.AddTackSprite(loc, texture,
                    new Rectangle(MannequinTackFrame.X, MannequinTackFrame.Y + splitRow,
                        MannequinTackFrame.Width, MannequinTackFrame.Height - splitRow),
                    origin + new Vector2(0f, splitRow * 4f), nudge);
            }
        }

        /// <summary>The layer depth the "Front" map layer draws the given tile row at, so a sprite
        /// given this depth is occluded by exactly the characters that tile would occlude. Mirrors
        /// xTile's Layer.DrawNormal, which uses (y*64 + 64 + sortOffset)/10000 with Game1 passing a
        /// sort offset of 64 for the first front layer.</summary>
        private static float FrontLayerDepth(int tileY) => (tileY * 64f + 64f + 64f) / 10000f;

        /// <summary>Per-sprite depth increment that lifts a tack sprite clear of the map tile it sits
        /// on without leaving that tile's band. Comfortably above float precision at these depths
        /// (~1.5e-8 near 0.19) and comfortably below the 1e-5 gap Game1 leaves between consecutive
        /// front layers, so tack can't leapfrog a Front2 tile on the same row.</summary>
        private const float TackDepthNudge = 2E-06f;

        private void AddTackSprite(GameLocation loc, Texture2D texture, Rectangle source, Vector2 worldPos, float depth)
        {
            // The empty texture name keeps the constructor off the content pipeline; the real
            // texture is a mod asset, assigned straight onto the public field afterwards.
            var sprite = new TemporaryAnimatedSprite("", source, worldPos, flipped: false, 0f, Color.White)
            {
                interval = 999999f,
                animationLength = 1,
                holdLastFrame = true,
                layerDepth = depth,
                scale = 4f,
            };
            sprite.texture = texture;
            sprite.sourceRect = source;

            loc.temporarySprites.Add(sprite);
            this.tackDisplaySprites.Value.Add(sprite);
        }

        /// <summary>Removes the mannequins' tack sprites.</summary>
        private void ClearTackDisplaySprites()
        {
            if (this.tackDisplaySprites.Value.Count == 0)
                return;

            GameLocation? loc = Game1.currentLocation;
            foreach (TemporaryAnimatedSprite sprite in this.tackDisplaySprites.Value)
                loc?.temporarySprites.Remove(sprite);
            this.tackDisplaySprites.Value.Clear();
        }
    }
}
