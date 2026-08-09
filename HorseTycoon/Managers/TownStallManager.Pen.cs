using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;
using StardewValley.TerrainFeatures;
using xTile.Layers;
using xTile.Tiles;

namespace HorseTycoon
{
    /// <summary>
    /// The paddock below the Town stall, up only on the days a horse vendor is in town. The horses
    /// standing in it are the ones actually on offer today — the Horse Seller's sale list or the Stud
    /// Master's stallions — so the pen always matches the shop menu, the same way the festival's
    /// market paddock does (see <c>FestivalRaceManager.SpawnDecorativeHorses</c>).
    /// </summary>
    public static partial class TownStallManager
    {
        // ====================================================================================
        // The fence
        // ====================================================================================

        /// <summary>Tile indices in Town's own outdoor tilesheet — the same fence that already stands
        /// in this corner: the tall run down x=118 and the three-panel stub at (109..111, 35).
        /// A horizontal run is two rows tall (rails on top, posts below) and needs the start/middle/end
        /// pieces; the vertical run is one index repeated.</summary>
        private static readonly int[] RailUpper = { 358, 359, 360 };
        private static readonly int[] RailLower = { 383, 384, 385 };

        /// <summary>The vertical run, top to bottom. Town's own run down x=118 just repeats the middle
        /// piece, but a short run needs the capped ends or it looks sawn off.</summary>
        private const int FencePostTop = 361;
        private const int FencePostMid = 386;
        private const int FencePostBottom = 436;

        /// <summary>Where the paddock's new sides go. Only two are ours: the top rail (upper art on
        /// <see cref="PenTopRail"/> on Front, posts on Buildings the row below) and the left run down
        /// <see cref="PenLeft"/>, running the full depth of the pen from the rail down to the cliff.
        /// <para>The other two sides are already there: Town's fence down x=118 closes the east side,
        /// and the cliff wall along y=37 closes the south. That leaves an interior of
        /// (112..117, 33..36) — and row 30 stays clear so the player can still stand at the counter.</para>
        /// <para>The rail crosses Town's fence and ends a tile past it, at x=119: ending *on* the fence
        /// put the end post half a tile shy of where the corner reads. Both crossings are snapshotted
        /// like everything else, so the fence comes back when the stall packs away.</para></summary>
        private const int PenLeft = 111;
        private const int PenRailRight = 119;
        private const int PenTopRail = 31;
        private const int PenLeftRunTop = PenTopRail;
        private const int PenLeftRunBottom = 36;

        /// <summary>Town's own vertical fence, which closes the paddock's east side. The rail crosses
        /// it and ends a tile beyond, so this is only here to read the fence's tilesheet off.</summary>
        private const int TownFenceX = 118;

        /// <summary>Bottom row of the paddock interior — the cliff wall is the row below.</summary>
        private const int PenInteriorBottom = 36;

        /// <summary>Every cell the paddock writes to, snapshotted with the stall's own footprint so the
        /// fence comes back down with it. Listed even on Jadu's days: nothing is written then, and
        /// re-recording untouched tiles costs nothing but keeps the teardown path identical.</summary>
        internal static IEnumerable<(string Layer, int X, int Y)> PenTiles
        {
            get
            {
                for (int x = PenLeft; x <= PenRailRight; x++)
                {
                    yield return ("Front", x, PenTopRail);
                    yield return ("Buildings", x, PenTopRail + 1);
                }

                for (int y = PenLeftRunTop; y <= PenLeftRunBottom; y++)
                    yield return (y == PenTopRail ? "Front" : "Buildings", PenLeft, y);
            }
        }

        /// <summary>Whether this vendor brings horses with them.</summary>
        private static bool VendorHasPaddock(TownVendor vendor) => vendor != TownVendor.Potions;

        private static string VendorSprite(TownVendor vendor) => vendor switch
        {
            TownVendor.HorseSeller => HorseSellerSprite,
            TownVendor.StudMaster => StudMasterSprite,
            _ => PotionSellerSprite,
        };

        /// <summary>Fences off the paddock. Runs after <see cref="FellCherryTree"/>, which is what
        /// makes room for it — the canopy used to hang over this whole corner.</summary>
        private static void BuildPen(GameLocation town)
        {
            TileSheet? sheet = GetFenceSheet(town);
            Layer? buildings = town.map?.GetLayer("Buildings");
            Layer? front = town.map?.GetLayer("Front");
            if (sheet == null || buildings == null || front == null)
            {
                Monitor.Log("Couldn't find Town's outdoor tilesheet, so the horse paddock wasn't fenced off. "
                    + "The stall itself is unaffected.", LogLevel.Warn);
                return;
            }

            // The rail's two rows go on different layers, and neither is AlwaysFront — that draws over
            // everything regardless of position, so the fence covered the heads of horses standing
            // inside the pen. The top half is on Front, which sorts by row like the rest of the world,
            // and the bottom half — the posts, on the row a horse at y=33 draws up into — is on
            // Buildings, so horses pass in front of it and that row is solid into the bargain.
            for (int x = PenLeft; x <= PenRailRight; x++)
            {
                int piece = x == PenLeft ? 0 : (x == PenRailRight ? 2 : 1);
                SetTile(front, x, PenTopRail, sheet, RailUpper[piece]);
                SetTile(buildings, x, PenTopRail + 1, sheet, RailLower[piece]);
            }

            // The left run, capped at both ends. It starts on the rail's own top row and overwrites the
            // rail's start piece, so the two runs meet at a single corner post rather than a rail post
            // with a second post slung underneath. That top tile has to go on Front like the rail row
            // it replaces — on Buildings the rail art would simply be drawn over it.
            for (int y = PenLeftRunTop; y <= PenLeftRunBottom; y++)
            {
                int piece = y == PenLeftRunTop ? FencePostTop
                    : y == PenLeftRunBottom ? FencePostBottom
                    : FencePostMid;
                SetTile(y == PenTopRail ? front : buildings, PenLeft, y, sheet, piece);
            }

            ClearPenClutter(town);
            Logger.LogVerbose($"Town paddock fenced off ({PenLeft}..{PenRailRight}, {PenTopRail}..{PenLeftRunBottom}).");
        }

        /// <summary>Clears the spawned clutter out of the paddock — grass tufts (which draw over the
        /// fence and the horses), weeds, stones and twigs. It's Town's usual seasonal debris rather
        /// than anything anyone placed, and it comes back on its own, so nothing is put back when the
        /// stall packs away.
        /// <para>Host only: <c>terrainFeatures</c> and <c>objects</c> are synced collections, and
        /// bushes and everything else are deliberately left alone.</para></summary>
        private static void ClearPenClutter(GameLocation town)
        {
            if (!Game1.IsMasterGame)
                return;

            int removed = 0;
            for (int x = PenLeft; x <= PenRailRight; x++)
            {
                for (int y = PenTopRail; y <= PenInteriorBottom; y++)
                {
                    var tile = new Vector2(x, y);

                    if (town.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature) && feature is Grass)
                    {
                        town.terrainFeatures.Remove(tile);
                        removed++;
                    }

                    if (town.objects.TryGetValue(tile, out StardewValley.Object? obj)
                        && (obj.IsWeeds() || obj.IsSpawnedObject || obj.Name is "Stone" or "Twig"))
                    {
                        town.objects.Remove(tile);
                        removed++;
                    }
                }
            }

            if (removed > 0)
                Logger.LogVerbose($"Cleared {removed} bits of grass/debris out of the Town paddock.");
        }

        /// <summary>Lists whatever is still standing in the paddock. <see cref="ClearPenClutter"/>
        /// deliberately only sweeps grass and debris, so if something is still drawing over the fence
        /// this says what it actually is — a bush in <c>largeTerrainFeatures</c>, say, which is scenery
        /// rather than clutter and isn't removed without asking.</summary>
        private static void DumpPenContents(GameLocation town)
        {
            var found = new List<string>();

            for (int x = PenLeft; x <= PenRailRight; x++)
            {
                for (int y = PenTopRail; y <= PenInteriorBottom; y++)
                {
                    var tile = new Vector2(x, y);
                    if (town.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature))
                        found.Add($"terrainFeature {feature.GetType().Name} at ({x},{y})");
                    if (town.objects.TryGetValue(tile, out StardewValley.Object? obj))
                        found.Add($"object '{obj.Name}' at ({x},{y})");
                }
            }

            foreach (LargeTerrainFeature large in town.largeTerrainFeatures)
            {
                Vector2 tile = large.Tile;
                if (tile.X >= PenLeft && tile.X <= PenRailRight && tile.Y >= PenTopRail && tile.Y <= PenInteriorBottom)
                    found.Add($"largeTerrainFeature {large.GetType().Name} at ({tile.X},{tile.Y})");
            }

            Monitor.Log(found.Count == 0
                ? $"Paddock ({PenLeft}..{PenRailRight}, {PenTopRail}..{PenInteriorBottom}) is clear of features and objects."
                : $"Still in the paddock: {string.Join(", ", found)}", LogLevel.Info);
        }

        private static void SetTile(Layer layer, int x, int y, TileSheet sheet, int index)
        {
            if (x < layer.LayerWidth && y < layer.LayerHeight)
                layer.Tiles[x, y] = new StaticTile(layer, sheet, BlendMode.Alpha, index);
        }

        /// <summary>The sheet the new fence is drawn from — taken off the fence already standing at
        /// (118,30) so the two runs can't end up on different tilesheets. Falls back to finding the
        /// season's outdoor sheet by name in case a Town-editing mod has moved that fence.</summary>
        private static TileSheet? GetFenceSheet(GameLocation town)
        {
            Layer? buildings = town.map?.GetLayer("Buildings");
            Tile? existing = (buildings != null && TownFenceX < buildings.LayerWidth)
                ? buildings.Tiles[TownFenceX, PenTopRail - 1]
                : null;
            if (existing?.TileSheet != null && existing.TileIndex == FencePostMid)
                return existing.TileSheet;

            return town.map?.TileSheets.FirstOrDefault(s =>
                s.ImageSource?.EndsWith("outdoorsTileSheet", StringComparison.OrdinalIgnoreCase) == true);
        }

        // ====================================================================================
        // The horses
        // ====================================================================================

        /// <summary>Marks a paddock horse, so strays can be cleared without holding a reference to
        /// them (a farmhand joining mid-day gets them from the host, not from this class).</summary>
        private const string PenHorseKey = "Froshty.HorseTycoon/TownPenHorse";

        /// <summary>Standing spots inside the (112..117, 33..36) interior — spread as a triangle so
        /// three horses don't look like a queue. There are exactly as many spots as a vendor brings
        /// (<see cref="TownOfferLimit"/>), so a full paddock is everything on offer today.</summary>
        private static readonly Point[] PenHorseSlots =
        {
            new(112, 33), new(116, 33), new(114, 35),
        };

        /// <summary>Which offer each paddock horse is showing, so it can be taken away once that offer
        /// is gone. Host-side only — see <see cref="SpawnPenHorses"/>.</summary>
        private static readonly Dictionary<Horse, HorseOffer> penHorses = new();

        /// <summary>Puts the paddock horses out, or clears them away on a day with no horse vendor.</summary>
        private static void RefreshPen(GameLocation town, bool standing, TownVendor vendor)
        {
            DespawnPenHorses(town);
            if (standing && VendorHasPaddock(vendor))
                SpawnPenHorses(town, vendor);
        }

        /// <summary>Fills the paddock with today's offers.
        /// <para>Host only. Unlike the festival's paddock — which stands in a temporary location that
        /// each client builds for itself — Town is a real, shared location, so these horses are added
        /// once by the host and reach farmhands through the usual character syncing.</para></summary>
        private static void SpawnPenHorses(GameLocation town, TownVendor vendor)
        {
            if (!Game1.IsMasterGame)
                return;

            // The same three the shop menu will show, so the paddock and the menu can't disagree.
            List<HorseOffer> offers = GetTownOffers(vendor).Where(o => o.IsAvailable).ToList();

            var rng = Utility.CreateDaySaveRandom(9931);
            for (int i = 0; i < PenHorseSlots.Length && i < offers.Count; i++)
            {
                Point tile = PenHorseSlots[i];
                HorseOffer offer = offers[i];

                var horse = new Horse(Guid.NewGuid(), tile.X, tile.Y) { Name = "TownPenHorse" };
                horse.modData[PenHorseKey] = offer.Name;
                // HorseOffer uses "" for the base skin, which is "Roan" as a horse skin id.
                horse.modData[HorseHelper.HorseSkinKey] = string.IsNullOrEmpty(offer.SkinId) ? "Roan" : offer.SkinId;
                // Renders bare-backed, and Horse_checkAction_Prefix reads the same flag to make the
                // horse unclickable — nobody gets to ride the merchandise.
                horse.modData[HorseHelper.NoTackKey] = "true";
                horse.currentLocation = town;
                horse.Position = new Vector2(tile.X * 64f, tile.Y * 64f);
                horse.Halt();
                horse.faceDirection(rng.Next(2) == 0 ? Game1.left : Game1.right);

                if (!town.characters.Contains(horse))
                    town.characters.Add(horse);
                HorseAnimations.SetGrazing(horse);
                penHorses[horse] = offer;
            }

            Logger.LogVerbose($"Town paddock stocked with {penHorses.Count} of {offers.Count} {vendor} offers.");
        }

        /// <summary>Clears the paddock. Sweeps by modData rather than by the tracked list so a horse
        /// left behind by an earlier session (or by a crash) still goes.</summary>
        private static void DespawnPenHorses(GameLocation town)
        {
            if (Game1.IsMasterGame)
            {
                foreach (Horse horse in town.characters.OfType<Horse>()
                             .Where(h => h.modData.ContainsKey(PenHorseKey))
                             .ToList())
                {
                    town.characters.Remove(horse);
                }
            }

            penHorses.Clear();
        }

        /// <summary>Takes a horse out of the paddock once its offer is gone, so the pen keeps matching
        /// the menu. Polled rather than driven by the purchase itself because a farmhand's purchase
        /// reaches the host as a message — see <c>HorseMarket</c>.</summary>
        private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (penHorses.Count == 0 || !Game1.IsMasterGame || !e.IsMultipleOf(30))
                return;

            GameLocation? town = Game1.getLocationFromName("Town");
            if (town == null)
                return;

            foreach ((Horse horse, HorseOffer offer) in penHorses.Where(p => !p.Value.IsAvailable).ToList())
            {
                town.characters.Remove(horse);
                penHorses.Remove(horse);
                Logger.LogVerbose($"Removed Town paddock horse '{offer.Name}' (offer no longer available).");
            }
        }
    }
}
