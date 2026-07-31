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
    /// <summary>
    /// Jadu's horse-supply stall in Town, set up on the days the Bookseller's wagon is in and packed
    /// away again afterwards. It sells the same goods he sells at the Summer Horse Festival — the
    /// counter carries an <c>OpenShop</c> tile action pointing at the festival's Data/Shops entry, so
    /// there is no shop code here at all.
    /// <para>The Bookseller isn't map data: <see cref="StardewValley.Locations.Town"/> draws the wagon
    /// straight from <c>mouseCursors_1_6</c> when today is one of the two days
    /// <see cref="Utility.getDaysOfBooksellerThisSeason"/> rolled for this save. Those days are seeded
    /// off the save file, so no Content Patcher token can gate an EditMap on them and the stall has to
    /// be stamped on from here with <see cref="GameLocation.ApplyMapOverride(Map, string, Rectangle?, Rectangle?, System.Action{Point})"/>.</para>
    /// </summary>
    public static class TownStallManager
    {
        /// <summary>The 4x5 stall map loaded by the CP pack (data/townstall.json). Its layer order is
        /// load-bearing — see the file's header.</summary>
        private const string StallMapAsset = "Maps/CP.HorseTycoon_TownHorseStall";

        /// <summary>Jadu's festival stock. Defined in the CP pack's data/ivpotions.json, which stays
        /// the single source of truth for what he sells (and for his shop-menu portrait/greeting).</summary>
        private const string ShopId = "CP.HorseTycoon_IsaacFestivalShop";

        // The stall is the whole 4x5 map: two awning rows and a frame row on AlwaysFront, the counter
        // top on Front, the counter face on Buildings.
        private static readonly Rectangle StallSource = new(0, 0, 4, 5);

        /// <summary>Where the stall lands in Town: the open dirt south-east of the Bookseller's wagon
        /// (which is drawn over tiles (106,22)-(112,26), with its balloon out to (115,26)), pushed off
        /// to the side so it doesn't block the walk up to the wagon.
        /// <para>The awning's top rows land on the notice board and the bench (Buildings, rows 25-26).
        /// That's fine and deliberate — the stall draws over them, and the map's layer order stops the
        /// override from deleting them.</para>
        /// <para>The right column sits on the cherry tree, whose canopy owns AlwaysFront
        /// (114..118, 28..31): wherever the stall paints Front or Buildings it also blanks AlwaysFront
        /// in the same cell, so the counter rows bite two canopy tiles at (114,28) and (114,29). That's
        /// accepted, and cosmetic — <see cref="RestoreSnapshot"/> puts the canopy back when the stall
        /// packs away. Dodging it entirely means either x=110 (clear of the tree) or y=23 (above the
        /// canopy, but colliding with the wagon).</para></summary>
        private static readonly Rectangle StallDest = new(111, 25, 4, 5);

        /// <summary>Tile Jadu stands on: behind the counter, so the counter-top row on the Front layer
        /// draws over his legs the way it does for Pierre at the Egg Festival.</summary>
        private static readonly Point KeeperTile = new(113, 28);

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
            helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
            {
                keeperSprite = null;
                snapshot.Clear();
                snapshotMap = null;
            };

            // The Bookseller's two days per season are rolled off the save seed, so there's no
            // reliable way to walk into the stall on demand — this puts it up (or takes it down)
            // without waiting for one.
            helper.ConsoleCommands.Add("ht_town_stall",
                "Sets up or packs away Jadu's horse stall in Town, ignoring whether the Bookseller is in.\n\n"
                + "Usage: ht_town_stall [on|off]\n"
                + "- Omit the argument to report today's Bookseller days and whether the stall is due.",
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
                Monitor.Log($"Stall tiles currently on Town's map: {IsStallOnMap(town)}. "
                    + $"Jadu placed: {keeperSprite != null}.", LogLevel.Info);
                Monitor.Log($"Run 'ht_town_stall on' to set it up regardless, then walk to "
                    + $"{StallDest.X},{StallDest.Y + StallDest.Height} in Town (north-east clearing, "
                    + "below the Bookseller's wagon).", LogLevel.Info);
                DumpFootprint(town);
                return;
            }

            bool open = args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
            ApplyOverride(town, open);
            RefreshKeeper(town, open);
            Monitor.Log($"Town horse stall {(open ? "set up" : "packed away")}; tiles on map: {IsStallOnMap(town)}.",
                LogLevel.Info);
        }

        /// <summary>Whether the Bookseller — and therefore the stall — is in town today.</summary>
        public static bool IsStallDay() => Utility.getDaysOfBooksellerThisSeason().Contains(Game1.dayOfMonth);

        private static void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            GameLocation? town = Game1.getLocationFromName("Town");
            if (town == null)
                return;

            bool open = IsStallDay();
            ApplyOverride(town, open);
            RefreshKeeper(town, open);
        }

        /// <summary>Puts the stall up, or takes it back down again.</summary>
        private static void ApplyOverride(GameLocation town, bool open)
        {
            if (!open)
            {
                RestoreSnapshot(town);
                return;
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
                return;
            }

            TakeSnapshot(town);

            // ApplyMapOverride refuses to run an override key twice for the life of a location
            // instance, so every stamp gets a fresh key: over a long session the stall has to be able
            // to go up on one Bookseller day, come down, and go up again on the next.
            string key = $"HorseTycoon_TownStall_{++overrideSerial}";
            town.ApplyMapOverride(stallMap, key, StallSource, StallDest);
            Logger.LogVerbose($"Town horse stall set up ({key}).");
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

            foreach (string layerName in StallLayers)
            {
                Layer? layer = town.map.GetLayer(layerName);
                if (layer == null)
                    continue;

                for (int x = StallDest.X; x < StallDest.Right; x++)
                {
                    for (int y = StallDest.Y; y < StallDest.Bottom; y++)
                    {
                        if (x < layer.LayerWidth && y < layer.LayerHeight)
                            snapshot.Add((layerName, x, y, layer.Tiles[x, y]));
                    }
                }
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

        /// <summary>Puts Jadu behind the counter, or takes him away on a day the stall isn't up.</summary>
        private static void RefreshKeeper(GameLocation town, bool open)
        {
            if (keeperSprite != null)
            {
                town.temporarySprites.Remove(keeperSprite);
                keeperSprite = null;
            }

            if (!open)
                return;

            Texture2D texture;
            try
            {
                // Always present: the CP pack bundles Jadu's sheet when SVE isn't installed
                // (data/festivalnpcs.json), same as it does for the festival stall keepers.
                texture = Game1.content.Load<Texture2D>("Characters\\Jadu");
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"Couldn't load Jadu's character sheet for the Town stall: {ex.Message}");
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

                // Essential, not cosmetic. He's added at DayStarted, before anyone is in Town, and
                // GameLocation.resetLocalState wipes every temporary sprite whose clearOnAreaEntry()
                // is true the moment a player walks in — which is the default. Without this Jadu is
                // deleted on entry and the stall is always unattended.
                dontClearOnAreaEntry = true,
            };
            sprite.texture = texture;
            sprite.sourceRect = source;

            town.temporarySprites.Add(sprite);
            keeperSprite = sprite;
        }
    }
}
