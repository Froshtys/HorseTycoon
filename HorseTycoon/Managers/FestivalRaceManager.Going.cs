using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// "Going" — per-tile ground that changes a racer's speed. Unlike the jump zones, this is authored
    /// purely by painting the map: the visible art IS the data. Mud patches slow a horse down, grass
    /// patches speed it up, for the player and the NPC racers alike, and only while the race is running.
    /// </summary>
    public partial class FestivalRaceManager
    {
        // Tile indices within the DesertTiles.png sheet. Mud is the 3x3 dark-brown patch at the top-left
        // of the sheet; grass is the tufts-on-sand tile on the 7th row, 3rd column.
        private static readonly HashSet<int> GoingHeavyTiles = new() { 0, 1, 2, 16, 17, 18, 32, 33, 34 };
        private static readonly HashSet<int> GoingFastTiles = new() { 98 };

        // Tilesheet ids (the tmx <tileset name=...>) that DesertTiles.png is mounted under. The map
        // declares it twice, and "desert-extended" is a DIFFERENT image, so match by id, not image source.
        private static readonly HashSet<string> GoingSheetIds = new() { "desert-new", "zdesertTiles" };

        // Scanned bottom→top: a mud/grass tile on a higher layer wins, and any other tile drawn over
        // one clears it, so the going always matches whatever the rider can actually see.
        private static readonly string[] GoingLayerOrder =
            { "Back", "Back2", "Back3", "Back4", "Buildings", "Buildings2" };

        /// <summary>Speed bonus per tile for the active festival map, in getMovementSpeed units
        /// (~1 tile/sec per point). Empty on maps with no going tiles.</summary>
        private static readonly Dictionary<Point, float> goingByTile = new();

        // Floor on a racer's final speed, so no stacking of penalties can bring a horse to a standstill.
        private const float MinRaceSpeed = 1f;

        /// <summary>Pixels travelled since the local rider's last hoofbeat. One beat per tile.</summary>
        private readonly PerScreen<float> hoofSoundTimer = new(() => 0f);
        /// <summary>Local rider's position at the previous tick, for measuring distance travelled.</summary>
        private readonly PerScreen<Vector2?> lastHoofPos = new(() => null);
        // Distance between hoofbeats, matching the NPC racers' cadence in UpdateNpcRacers.
        private const float HoofBeatDistance = 64f;

        /// <summary>
        /// Builds <see cref="goingByTile"/> from the festival map's ground art. Called once per race
        /// alongside <see cref="LoadNpcJumpZonesFromMap"/>; maps without any of the marker tiles (Forest,
        /// Fall beach) simply produce an empty table and are unaffected.
        /// </summary>
        private void LoadGoingFromMap(GameLocation loc)
        {
            goingByTile.Clear();

            foreach (string layerName in GoingLayerOrder)
            {
                var layer = loc.map.GetLayer(layerName);
                if (layer == null)
                    continue;

                for (int y = 0; y < layer.LayerHeight; y++)
                    for (int x = 0; x < layer.LayerWidth; x++)
                    {
                        var tile = layer.Tiles[x, y];
                        if (tile?.TileSheet == null)
                            continue;

                        var point = new Point(x, y);
                        if (GoingSheetIds.Contains(tile.TileSheet.Id) && GoingHeavyTiles.Contains(tile.TileIndex))
                            goingByTile[point] = Def.GoingHeavyBonus;
                        else if (GoingSheetIds.Contains(tile.TileSheet.Id) && GoingFastTiles.Contains(tile.TileIndex))
                            goingByTile[point] = Def.GoingFastBonus;
                        else
                            goingByTile.Remove(point); // covered by other art — plain ground again
                    }
            }

            if (goingByTile.Count == 0)
                return;

            int heavy = 0, fast = 0;
            foreach (float bonus in goingByTile.Values)
            {
                if (bonus < 0f) heavy++;
                else if (bonus > 0f) fast++;
            }
            Logger.LogVerbose($"Going loaded: {heavy} heavy (mud) tile(s), {fast} fast (grass) tile(s).");
        }

        /// <summary>
        /// Additive speed bonus for the ground at <paramref name="tile"/> in getMovementSpeed units
        /// (~1 tile/sec per point), or 0 outside the race / off a going tile. Must never throw — it feeds
        /// getMovementSpeed, which drives position extrapolation.
        /// </summary>
        private static float GoingSpeedBonusAt(Vector2 tile)
        {
            if (goingByTile.Count == 0 || !IsInAnyRacingPhase)
                return 0f;
            return goingByTile.TryGetValue(new Point((int)tile.X, (int)tile.Y), out float bonus) ? bonus : 0f;
        }

        /// <summary>
        /// Plays one hoofbeat for the ground at <paramref name="tile"/>. The beat itself is always
        /// vanilla's mounted hoof sound, mirroring Horse.PerformDefaultHorseFootstep — "thudStep" on
        /// everything but stone and wood. (The farmer's own footsteps, "sandyStep"/"grassyStep", are a
        /// soft shuffle and don't read as hooves.) The going then layers its character on top: a splash
        /// through mud, a swish through grass, so you hear the surface without losing the hoofbeat.
        /// </summary>
        private static void PlayHoofstep(GameLocation loc, Vector2 tile)
        {
            string beat = loc.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Type", "Back") switch
            {
                "Stone" => "stoneStep",
                "Wood" => "woodyStep",
                _ => "thudStep",
            };
            loc.localSound(beat, tile);

            float going = GoingSpeedBonusAt(tile);
            if (going < 0f)
                loc.localSound("waterSlosh", tile);
            else if (going > 0f)
                loc.localSound("grassyStep", tile);
        }

        /// <summary>
        /// Hoofbeats for the local rider, one per tile travelled. Vanilla plays these from frame
        /// callbacks on the mounted-gallop animation (Horse.OnMountFootstep), but the race drives the
        /// horse sprite itself via SetGalloppingAnimation, which has no such callbacks — hence silence.
        /// Driving them off distance travelled instead also matches the NPC racers' cadence exactly.
        /// </summary>
        private void UpdateHoofSounds()
        {
            if (!Game1.player.isRidingHorse())
            {
                this.lastHoofPos.Value = null;
                return;
            }

            Vector2 pos = Game1.player.Position;
            Vector2? previous = this.lastHoofPos.Value;
            this.lastHoofPos.Value = pos;
            if (previous == null)
                return;

            // Silence mid-jump: hooves are off the ground until the landing.
            if (Game1.player.yJumpVelocity != 0f || Game1.player.yJumpOffset != 0)
                return;

            // A jump over more than a tile in one tick is a teleport (lineup, DQ arrival), not a stride.
            float moved = Vector2.Distance(previous.Value, pos);
            if (moved <= 0f || moved > HoofBeatDistance)
                return;

            this.hoofSoundTimer.Value -= moved;
            if (this.hoofSoundTimer.Value > 0f)
                return;

            this.hoofSoundTimer.Value += HoofBeatDistance;
            PlayHoofstep(Game1.currentLocation, Game1.player.Tile);
        }
    }
}
