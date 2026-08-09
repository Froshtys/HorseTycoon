using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.Tiles;

namespace HorseTycoon
{
    /// <summary>Who is behind the Town stall's counter today. One is rolled per Bookseller day off
    /// the save seed, so every client in a multiplayer game agrees without a message.</summary>
    public enum TownVendor
    {
        /// <summary>Jadu, selling the same Gold Carrot seeds and IV potion he sells at the festival
        /// (a plain Data/Shops shop, opened by the stall map's own tile action).</summary>
        Potions,

        /// <summary>Alesia, with today's sale horses standing in the paddock below the stall.</summary>
        HorseSeller,

        /// <summary>Isaac, with today's stallions in the paddock; also buys stud services.</summary>
        StudMaster,
    }

    /// <summary>
    /// A travelling horse-trade stall in Town, set up on the days the Bookseller's wagon is in and
    /// packed away again afterwards. One of three vendors is behind the counter each visit (see
    /// <see cref="TownVendor"/>), all of them lifted from the Summer Horse Festival's market: Jadu with
    /// his potions, or one of the two horse traders — who bring a paddock of the horses they're
    /// offering with them (<c>TownStallManager.Pen.cs</c>, <c>TownStallManager.Shops.cs</c>).
    /// <para>The Bookseller isn't map data: <see cref="StardewValley.Locations.Town"/> draws the wagon
    /// straight from <c>mouseCursors_1_6</c> when today is one of the two days
    /// <see cref="Utility.getDaysOfBooksellerThisSeason"/> rolled for this save. Those days are seeded
    /// off the save file, so no Content Patcher token can gate an EditMap on them and the stall has to
    /// be stamped on from here with <see cref="GameLocation.ApplyMapOverride(Map, string, Rectangle?, Rectangle?, System.Action{Point})"/>.</para>
    /// </summary>
    public static partial class TownStallManager
    {
        /// <summary>The 4x5 stall map loaded by the CP pack (data/townstall.json). Its layer order is
        /// load-bearing — see the file's header.</summary>
        private const string StallMapAsset = "Maps/CP.HorseTycoon_TownHorseStall";

        /// <summary>Jadu's festival stock. Defined in the CP pack's data/ivpotions.json, which stays
        /// the single source of truth for what he sells (and for his shop-menu portrait/greeting).</summary>
        private const string ShopId = "CP.HorseTycoon_IsaacFestivalShop";

        /// <summary>Character sheet (and shop-menu portrait) for each vendor. The same three sprites
        /// the summer festival's stalls use — the CP pack bundles them when SVE isn't installed.</summary>
        private const string PotionSellerSprite = "Jadu";
        private const string HorseSellerSprite = "Alesia";
        private const string StudMasterSprite = "Isaac";

        /// <summary>Tile action stamped onto the counter when a horse vendor is in, replacing the
        /// stall map's own <c>OpenShop</c> action. Jadu's shop is plain Data/Shops content, but the
        /// horse shops are menus in code, so they need a handler — see <see cref="Initialize"/>.</summary>
        private const string CounterAction = "HorseTycoon_TownStall";

        /// <summary>Set by <c>ht_town_stall on &lt;vendor&gt;</c>; overrides the daily roll until the
        /// day rolls over or the stall is forced up again without a vendor.</summary>
        private static TownVendor? forcedVendor;

        // The stall is the whole 4x5 map: two awning rows and a frame row on AlwaysFront, the counter
        // top on Front, the counter face on Buildings.
        private static readonly Rectangle StallSource = new(0, 0, 4, 5);

        /// <summary>Where the stall lands in Town: the open dirt east of the Bookseller's wagon, which
        /// is drawn over tiles (106,22)-(112,26) and whose widest piece (the banner along its roof)
        /// stops at x=114 on row 23 — so this clears it, and it doesn't block the walk up to the wagon.
        /// <para>The awning's top rows land on the notice board (Buildings, rows 25-26). That's fine and
        /// deliberate — the stall draws over it, and the map's layer order stops the override from
        /// deleting it.</para>
        /// <para>The stall stands where the cherry tree is; the tree comes down with it, see
        /// <see cref="CherryTreeTiles"/>.</para></summary>
        private static readonly Rectangle StallDest = new(114, 25, 4, 5);

        /// <summary>Tile Jadu stands on: behind the counter, so the counter-top row on the Front layer
        /// draws over his legs the way it does for Pierre at the Egg Festival.</summary>
        private static readonly Point KeeperTile = new(116, 28);

        /// <summary>The cherry tree the stall stands in — canopy on AlwaysFront, trunk on Front. It is
        /// felled while the stall is up rather than drawn around: the canopy hangs over the counter and
        /// both awning rows, and cutting only the cells the stall covers leaves a half-eaten tree with
        /// its trunk still standing below. <see cref="RestoreSnapshot"/> plants it again when the stall
        /// packs away.
        /// <para>(119,28) is deliberately left in place. The canopy is a clean 6x4 block everywhere else,
        /// but Town paints the museum's roof corner over that one cell, so clearing it would punch a
        /// hole in the building instead of taking away a leaf.</para>
        /// <para>The trunk — (116..117, 32..34) — is cleared on every layer the stall knows about rather
        /// than on the one layer vanilla Town.tmx puts it on. Vanilla splits it across Front (its top
        /// row) and Buildings (the two below), and a Town-editing mod is free to split it somewhere
        /// else: clearing only Front left the blossomed top of the trunk standing. Those six cells hold
        /// nothing but tree, so blanking all three layers costs nothing.</para></summary>
        private static IEnumerable<(string Layer, int X, int Y)> CherryTreeTiles
        {
            get
            {
                for (int y = 28; y <= 31; y++)
                {
                    for (int x = 114; x <= 119; x++)
                    {
                        if (x != 119 || y != 28)
                            yield return ("AlwaysFront", x, y);
                    }
                }

                foreach (string layerName in StallLayers)
                {
                    for (int y = 32; y <= 34; y++)
                    {
                        for (int x = 116; x <= 117; x++)
                            yield return (layerName, x, y);
                    }
                }
            }
        }

        /// <summary>Jadu himself, as a temporary sprite rather than a real NPC — he needs no schedule,
        /// no dialogue and no place in Town's saved character list.</summary>
        private static TemporaryAnimatedSprite? keeperSprite;

        /// <summary>Makes each override key unique — see the comment in <see cref="ApplyOverride"/>.</summary>
        private static int overrideSerial;

        private static IModHelper Helper = null!;
        private static IMonitor Monitor = null!;

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            Helper = helper;
            Monitor = monitor;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
            {
                keeperSprite = null;
                snapshot.Clear();
                snapshotMap = null;
                forcedVendor = null;
                penHorses.Clear();
                soldStudIds.Clear();
            };

            // Jadu's counter opens a Data/Shops shop straight from the map's tile action, but the
            // horse vendors' shops are menus in code, so their counter gets this action instead.
            GameLocation.RegisterTileAction(CounterAction, (_, args, _, _) =>
            {
                OpenVendorShop(args.Length > 1 && Enum.TryParse(args[1], out TownVendor vendor)
                    ? vendor
                    : GetVendor());
                return true;
            });

            // The Bookseller's two days per season are rolled off the save seed, so there's no
            // reliable way to walk into the stall on demand — this puts it up (or takes it down)
            // without waiting for one.
            helper.ConsoleCommands.Add("ht_town_stall",
                "Sets up or packs away the travelling horse stall in Town, ignoring whether the Bookseller is in.\n\n"
                + "Usage: ht_town_stall [on|off] [potions|horseseller|studmaster]\n"
                + "- The vendor is rolled per Bookseller day; name one to force it (the horse vendors\n"
                + "  bring the paddock and its horses with them).\n"
                + "- Omit all arguments to report today's Bookseller days and whether the stall is due.",
                HandleStallCommand);
        }

        private static void HandleStallCommand(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                Monitor.Log("Load a save first.", LogLevel.Warn);
                return;
            }

            GameLocation? town = Game1.getLocationFromName("Town");
            if (town == null)
            {
                Monitor.Log("Town isn't loaded.", LogLevel.Warn);
                return;
            }

            if (args.Length == 0)
            {
                string days = string.Join(", ", Utility.getDaysOfBooksellerThisSeason());
                Monitor.Log($"Bookseller visits {Game1.season} {days} this year; today is day {Game1.dayOfMonth}, "
                    + $"so the stall is {(IsStallDay() ? "due" : "NOT due")}.", LogLevel.Info);
                Monitor.Log($"Today's vendor: {GetVendor()}"
                    + (forcedVendor != null ? " (forced from the console)" : "")
                    + $"; horses in the paddock: {penHorses.Count}.", LogLevel.Info);
                Monitor.Log($"Stall tiles currently on Town's map: {IsStallOnMap(town)}. "
                    + $"Keeper placed: {keeperSprite != null}.", LogLevel.Info);
                Monitor.Log($"Run 'ht_town_stall on' to set it up regardless, then walk to "
                    + $"{StallDest.X},{StallDest.Y + StallDest.Height} in Town (north-east clearing, "
                    + "below the Bookseller's wagon).", LogLevel.Info);
                DumpFootprint(town);
                DumpPenContents(town);
                return;
            }

            bool open = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
            if (args.Length > 1)
            {
                if (!Enum.TryParse(args[1], ignoreCase: true, out TownVendor requested))
                {
                    Monitor.Log($"Unknown vendor '{args[1]}'. Use potions, horseseller or studmaster.", LogLevel.Warn);
                    return;
                }
                forcedVendor = requested;
            }
            else if (open)
            {
                forcedVendor = null;
            }

            TownVendor vendor = GetVendor();
            bool standing = ApplyOverride(town, open, vendor);
            RefreshKeeper(town, standing, vendor);
            RefreshPen(town, standing, vendor);
            Monitor.Log($"Town horse stall {(open ? $"set up ({vendor})" : "packed away")}; "
                + $"tiles on map: {IsStallOnMap(town)}, horses in the paddock: {penHorses.Count}.",
                LogLevel.Info);
        }

        /// <summary>Whether the Bookseller — and therefore the stall — is in town today.</summary>
        public static bool IsStallDay() => Utility.getDaysOfBooksellerThisSeason().Contains(Game1.dayOfMonth);

        /// <summary>Who is behind the counter today. Rolled off the save seed and the date, so it needs
        /// no syncing: every client rolls the same vendor, and it stays the same all day.</summary>
        public static TownVendor GetVendor()
            => forcedVendor ?? (TownVendor)Utility.CreateDaySaveRandom(8123).Next(3);

        private static void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            GameLocation? town = Game1.getLocationFromName("Town");
            if (town == null)
                return;

            forcedVendor = null;
            soldStudIds.Clear();

            bool open = IsStallDay();
            TownVendor vendor = GetVendor();
            bool standing = ApplyOverride(town, open, vendor);
            RefreshKeeper(town, standing, vendor);
            RefreshPen(town, standing, vendor);
        }

        /// <summary>Clears the paddock before the save is written. The map override and Jadu are both
        /// throwaway (maps aren't saved, and he's a temporary sprite), but the paddock horses are real
        /// characters in a real location and would otherwise be saved into Town overnight.</summary>
        private static void OnDayEnding(object? sender, DayEndingEventArgs e)
        {
            GameLocation? town = Game1.getLocationFromName("Town");
            if (town != null)
                DespawnPenHorses(town);
        }

        /// <summary>Puts the stall up, or takes it back down again. Returns whether the stall is
        /// standing afterwards — the caller uses that to decide whether to place Jadu, so a stall that
        /// failed to load doesn't leave him serving customers from an empty patch of dirt.</summary>
        private static bool ApplyOverride(GameLocation town, bool open, TownVendor vendor)
        {
            if (!open)
            {
                RestoreSnapshot(town);
                return false;
            }

            Map stallMap;
            try
            {
                // GameContent, not Game1.content or xTileContent: this asset only exists because the
                // CP pack provides it, and this is the route that's guaranteed to see mod-provided
                // assets. A failure here means the pack didn't load, so it's an error, not chatter.
                stallMap = Helper.GameContent.Load<Map>(StallMapAsset);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Couldn't load the Town stall map '{StallMapAsset}' — is the [CP] HorseTycoon "
                    + $"pack installed and enabled? {ex.Message}", LogLevel.Error);
                return false;
            }

            TakeSnapshot(town);

            // ApplyMapOverride refuses to run an override key twice for the life of a location
            // instance, so every stamp gets a fresh key: over a long session the stall has to be able
            // to go up on one Bookseller day, come down, and go up again on the next.
            string key = $"HorseTycoon_TownStall_{++overrideSerial}";
            town.ApplyMapOverride(stallMap, key, StallSource, StallDest);

            // Check the tiles actually landed rather than trusting that no exception means success —
            // a map whose tilesheet couldn't be resolved loads fine and then draws nothing.
            if (!IsStallOnMap(town))
            {
                Monitor.Log($"The Town stall map loaded but its tiles didn't land. The usual cause is a "
                    + "tilesheet that couldn't be resolved — check assets/maps/Festivals.png exists as a "
                    + "real file (it's a symlink in the repo, so a hand-copied pack can dangle).",
                    LogLevel.Error);
                RestoreSnapshot(town);
                return false;
            }

            FellCherryTree(town);
            RetargetCounterAction(town, vendor);
            if (VendorHasPaddock(vendor))
                BuildPen(town);

            Logger.LogVerbose($"Town horse stall set up ({key}, {vendor}).");
            return true;
        }

        /// <summary>Points the counter at the right shop. The stall map ships with Jadu's
        /// <c>OpenShop</c> action baked into its four counter tiles, so on a horse vendor's day those
        /// tiles are re-pointed at <see cref="CounterAction"/> instead.
        /// <para>Safe to edit in place: ApplyMapOverride hands the destination a brand-new StaticTile
        /// with its own copy of the properties, so this can't leak back into the cached stall map.</para></summary>
        private static void RetargetCounterAction(GameLocation town, TownVendor vendor)
        {
            if (vendor == TownVendor.Potions)
                return;

            Layer? buildings = town.map?.GetLayer("Buildings");
            if (buildings == null)
                return;

            int y = StallDest.Bottom - 1;
            for (int x = StallDest.X; x < StallDest.Right; x++)
            {
                Tile? tile = (x < buildings.LayerWidth && y < buildings.LayerHeight) ? buildings.Tiles[x, y] : null;
                if (tile != null)
                    tile.Properties["Action"] = $"{CounterAction} {vendor}";
            }
        }

        /// <summary>Town's own tiles under the stall's footprint, kept so the stall can be taken down
        /// without guessing what was there. Paired with the <see cref="Map"/> they came from, since
        /// they hold references to that map's tilesheets and mean nothing against a reloaded one.</summary>
        private static Map? snapshotMap;
        private static readonly List<(string Layer, int X, int Y, Tile? Tile)> snapshot = new();

        /// <summary>Records what the stall is about to cover.
        /// <para>This is why there's no "blank rectangle" version of the stall map. A blank rectangle
        /// tears the stall down by relying on ApplyMapOverride's null tiles being destructive, which
        /// erases everything in the footprint — including the bench and the tree canopy the stall was
        /// only supposed to be standing in front of. Putting the exact tiles back is both simpler and
        /// correct, and it leaves the stall map free to use whatever layer order suits it.</para>
        /// </summary>
        private static void TakeSnapshot(GameLocation town)
        {
            // Already up (a second stamp without a teardown) — the current tiles are the stall's own,
            // so keep the snapshot that actually holds Town's.
            if (snapshotMap == town.map && snapshot.Count > 0)
                return;

            snapshot.Clear();
            snapshotMap = town.map;
            if (town.map == null)
                return;

            // The tree overlaps the footprint, so the same cell can be listed twice — recording it
            // twice would be harmless (both copies hold Town's own tile), but it makes the snapshot
            // count meaningless when reading the log.
            var seen = new HashSet<(string, int, int)>();

            foreach ((string layerName, int x, int y) in SnapshotCells())
            {
                Layer? layer = town.map.GetLayer(layerName);
                if (layer == null || x >= layer.LayerWidth || y >= layer.LayerHeight)
                    continue;

                if (seen.Add((layerName, x, y)))
                    snapshot.Add((layerName, x, y, layer.Tiles[x, y]));
            }
        }

        /// <summary>Every cell the stall disturbs: its own footprint on each layer it paints, the
        /// cherry tree it fells, and the paddock fence below it.</summary>
        private static IEnumerable<(string Layer, int X, int Y)> SnapshotCells()
        {
            foreach (string layerName in StallLayers)
            {
                for (int x = StallDest.X; x < StallDest.Right; x++)
                {
                    for (int y = StallDest.Y; y < StallDest.Bottom; y++)
                        yield return (layerName, x, y);
                }
            }

            foreach ((string Layer, int X, int Y) cell in CherryTreeTiles)
                yield return cell;

            foreach ((string Layer, int X, int Y) cell in PenTiles)
                yield return cell;
        }

        /// <summary>Clears the cherry tree so the stall isn't standing under a canopy. Runs after the
        /// override, but never fights it: the stall's footprint stops at row 29 and the only cells this
        /// touches inside it are AlwaysFront ones the override has already blanked.</summary>
        private static void FellCherryTree(GameLocation town)
        {
            foreach ((string layerName, int x, int y) in CherryTreeTiles)
            {
                Layer? layer = town.map?.GetLayer(layerName);
                if (layer != null && x < layer.LayerWidth && y < layer.LayerHeight)
                    layer.Tiles[x, y] = null;
            }
        }

        /// <summary>Writes Town's own tiles back over the stall.</summary>
        private static void RestoreSnapshot(GameLocation town)
        {
            if (snapshot.Count == 0)
                return;

            // A reloaded map (season change, or a fresh session) is already vanilla here, and the
            // saved tiles point at the old map's tilesheets — putting them back would be wrong.
            if (!ReferenceEquals(snapshotMap, town.map))
            {
                snapshot.Clear();
                snapshotMap = null;
                Logger.LogVerbose("Town's map was reloaded since the stall went up; nothing to take down.");
                return;
            }

            foreach ((string layerName, int x, int y, Tile? tile) in snapshot)
            {
                Layer? layer = town.map?.GetLayer(layerName);
                if (layer != null && x < layer.LayerWidth && y < layer.LayerHeight)
                    layer.Tiles[x, y] = tile;
            }

            snapshot.Clear();
            snapshotMap = null;
            Logger.LogVerbose("Town horse stall packed away.");
        }

        /// <summary>Layers the stall paints on, in the order Town draws them.</summary>
        private static readonly string[] StallLayers = { "Buildings", "Front", "AlwaysFront" };

        /// <summary>Prints what is actually sitting on each of the stall's twenty tiles, per layer.
        /// The stall spans three layers (awning on AlwaysFront, counter top on Front, counter face on
        /// Buildings), so when part of it doesn't render this says which row failed to land rather
        /// than leaving it to guesswork.</summary>
        private static void DumpFootprint(GameLocation town)
        {
            Monitor.Log($"Stall footprint {StallDest.X},{StallDest.Y} {StallDest.Width}x{StallDest.Height}:",
                LogLevel.Info);
            foreach (string layerName in StallLayers)
            {
                Layer? layer = town.map?.GetLayer(layerName);
                if (layer == null)
                {
                    Monitor.Log($"  {layerName}: LAYER MISSING from Town's map.", LogLevel.Warn);
                    continue;
                }

                for (int y = StallDest.Y; y < StallDest.Bottom; y++)
                {
                    var row = new List<string>();
                    for (int x = StallDest.X; x < StallDest.Right; x++)
                    {
                        Tile? tile = (x < layer.LayerWidth && y < layer.LayerHeight) ? layer.Tiles[x, y] : null;
                        row.Add(tile == null ? "----" : $"{tile.TileIndex}@{tile.TileSheet?.Id ?? "?"}");
                    }
                    Monitor.Log($"  {layerName} y={y}: {string.Join("  ", row)}", LogLevel.Info);
                }
            }

            // Whatever is left of the cherry tree, so "the trunk is still there" can be traced to the
            // layer it's actually on rather than the layer vanilla Town.tmx puts it on.
            var standing = new List<string>();
            foreach ((string layerName, int x, int y) in CherryTreeTiles)
            {
                Layer? layer = town.map?.GetLayer(layerName);
                Tile? tile = (layer != null && x < layer.LayerWidth && y < layer.LayerHeight)
                    ? layer.Tiles[x, y]
                    : null;
                if (tile != null)
                    standing.Add($"{layerName}({x},{y})={tile.TileIndex}@{tile.TileSheet?.Id ?? "?"}");
            }
            Monitor.Log(standing.Count == 0
                ? "Cherry tree: fully cleared."
                : $"Cherry tree still standing on {standing.Count} tiles: {string.Join(", ", standing)}",
                LogLevel.Info);
        }

        /// <summary>Whether the stall's counter is currently on Town's map. Used by the console
        /// command to tell "the override never ran" apart from "it ran and something undid it".</summary>
        private static bool IsStallOnMap(GameLocation town)
        {
            Layer? buildings = town.map?.GetLayer("Buildings");
            return buildings != null
                && StallDest.X < buildings.LayerWidth
                && StallDest.Bottom - 1 < buildings.LayerHeight
                && buildings.Tiles[StallDest.X, StallDest.Bottom - 1] != null;
        }

        /// <summary>Puts today's vendor behind the counter, or takes them away on a day the stall
        /// isn't up.</summary>
        private static void RefreshKeeper(GameLocation town, bool open, TownVendor vendor)
        {
            if (keeperSprite != null)
            {
                town.temporarySprites.Remove(keeperSprite);
                keeperSprite = null;
            }

            if (!open)
                return;

            string spriteName = VendorSprite(vendor);
            Texture2D texture;
            try
            {
                // Always present: the CP pack bundles these sheets when SVE isn't installed
                // (data/festivalnpcs.json), same as it does for the festival stall keepers.
                texture = Game1.content.Load<Texture2D>("Characters\\" + spriteName);
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"Couldn't load '{spriteName}' character sheet for the Town stall: {ex.Message}");
                return;
            }

            // Frame 0 of a 16x32 character sheet is the front-facing idle pose. Drawn a tile high so
            // his feet land on KeeperTile.
            var source = new Rectangle(0, 0, 16, 32);
            var worldPos = new Vector2(KeeperTile.X * 64f, (KeeperTile.Y - 1) * 64f);

            // Between the Buildings pass (the counter front) and the Front layer at his own row
            // (which Game1 draws at (y * 64 + 128) / 10000), so the counter top hides his legs.
            float depth = (KeeperTile.Y * 64f + 32f) / 10000f;

            // The empty texture name keeps the constructor off the content pipeline; the real texture
            // is assigned straight onto the public field afterwards (same as the festival tack display).
            var sprite = new TemporaryAnimatedSprite("", source, worldPos, flipped: false, 0f, Color.White)
            {
                interval = 999999f,
                animationLength = 1,
                holdLastFrame = true,
                layerDepth = depth,
                scale = 4f,

                // Essential, not cosmetic. The keeper is added at DayStarted, before anyone is in
                // Town, and GameLocation.resetLocalState wipes every temporary sprite whose
                // clearOnAreaEntry() is true the moment a player walks in — which is the default.
                // Without this they're deleted on entry and the stall is always unattended.
                dontClearOnAreaEntry = true,
            };
            sprite.texture = texture;
            sprite.sourceRect = source;

            town.temporarySprites.Add(sprite);
            keeperSprite = sprite;
        }
    }
}
