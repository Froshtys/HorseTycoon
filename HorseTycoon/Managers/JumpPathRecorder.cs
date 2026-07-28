using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// Debug authoring tool for NPC race routes and jump zones. Run <c>ht_record_jumps</c> in-game to
    /// start recording; run it again to stop and dump a summary.
    ///
    /// While recording (mounted), it logs:
    ///  - a normal waypoint every <see cref="WaypointIntervalMs"/> ms at the current horse tile,
    ///  - a jump takeoff waypoint (approach tile) whenever the player jumps forward, and
    ///  - a jump landing waypoint (landing tile) where that jump ends.
    ///
    /// Takeoff/landing tiles are paired into jump zones. On stop, it prints the full ordered route
    /// (paste into <c>FestivalDefinition.NpcRaceRoutes</c>) plus the approach/landing tile lists,
    /// which are then placed on the map's <c>NpcJumpApproach</c>/<c>NpcJumpLanding</c> layers in Tiled.
    /// </summary>
    internal static class JumpPathRecorder
    {
        private const string Prefix = "[JumpRec]";
        private const float WaypointIntervalMs = 3000f;

        private static IModHelper Helper = null!;
        private static IMonitor Monitor = null!;

        private static bool recording;
        private static float waypointTimerMs;
        private static bool prevAirborne;
        private static Point takeoffTile;
        private static string locationName = "";

        // Ordered path of tiles (waypoints + jump takeoff/landing points), consecutive duplicates removed.
        private static readonly List<Point> route = new();
        // Paired jump zones in the order they occurred.
        private static readonly List<(Point Approach, Point Landing)> jumps = new();
        private static int waypointCount;

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            Helper = helper;
            Monitor = monitor;

            helper.ConsoleCommands.Add(
                "ht_record_jumps",
                "Toggles recording of an NPC race path (waypoints every 3s + jump takeoff/landing tiles).\n"
                + "Run once to start (while mounted on the course), run again to stop and dump the route + jump zones.",
                (_, _) => Toggle());
        }

        private static void Toggle()
        {
            if (!Context.IsWorldReady)
            {
                Monitor.Log("Load a save first.", LogLevel.Warn);
                return;
            }

            if (recording)
                Stop();
            else
                Start();
        }

        private static void Start()
        {
            route.Clear();
            jumps.Clear();
            waypointCount = 0;
            waypointTimerMs = 0f;
            prevAirborne = IsAirborne();
            locationName = Game1.currentLocation?.NameOrUniqueName ?? "(unknown)";

            recording = true;
            Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            // Seed the route with the current tile so the path starts where the player is.
            Point start = CurrentTile();
            route.Add(start);
            Monitor.Log($"{Prefix} Recording STARTED in '{locationName}'. Start tile: {Fmt(start)}. Run ht_record_jumps again to stop.", LogLevel.Info);
            if (!Game1.player.isRidingHorse())
                Monitor.Log($"{Prefix} Note: you are not mounted. Jumps are only detected while riding a horse.", LogLevel.Warn);
        }

        private static void Stop()
        {
            recording = false;
            Helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            DumpSummary();
        }

        private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
            {
                Stop();
                return;
            }

            // Auto-stop and dump if the player leaves the location the recording began in, so a run
            // ending by warping out (e.g. race finish teleport) still produces usable output.
            string here = Game1.currentLocation?.NameOrUniqueName ?? "(unknown)";
            if (here != locationName)
            {
                Monitor.Log($"{Prefix} Location changed ({locationName} -> {here}); stopping recording.", LogLevel.Info);
                Stop();
                return;
            }

            // Jump detection: rising edge = takeoff, falling edge = landing. The player's position is
            // driven along the jump arc, so the tile at takeoff is the approach and the tile at landing
            // is the destination. A jump that starts and ends on the same tile (blocked/in-place hop) is
            // recorded as an ordinary waypoint, not a jump zone.
            bool airborne = IsAirborne();
            if (airborne && !prevAirborne)
            {
                takeoffTile = CurrentTile();
            }
            else if (!airborne && prevAirborne)
            {
                Point landing = CurrentTile();
                if (landing != takeoffTile)
                {
                    jumps.Add((takeoffTile, landing));
                    AddToRoute(takeoffTile);
                    AddToRoute(landing);
                    Monitor.Log($"{Prefix} Jump #{jumps.Count}: takeoff {Fmt(takeoffTile)} -> landing {Fmt(landing)}", LogLevel.Info);
                }
                else
                {
                    AddToRoute(landing);
                    Monitor.Log($"{Prefix} Hop (no horizontal travel) at {Fmt(landing)}, recorded as waypoint only.", LogLevel.Info);
                }
                // Reset the periodic timer so a waypoint doesn't fire immediately after a jump point.
                waypointTimerMs = 0f;
            }
            prevAirborne = airborne;

            // Periodic waypoint every WaypointIntervalMs (skip while airborne so jump points stay clean).
            if (!airborne)
            {
                waypointTimerMs += (float)Game1.currentGameTime.ElapsedGameTime.TotalMilliseconds;
                if (waypointTimerMs >= WaypointIntervalMs)
                {
                    waypointTimerMs -= WaypointIntervalMs;
                    Point tile = CurrentTile();
                    if (AddToRoute(tile))
                    {
                        waypointCount++;
                        Monitor.Log($"{Prefix} Waypoint #{waypointCount}: {Fmt(tile)}", LogLevel.Info);
                    }
                }
            }
        }

        private static void DumpSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Prefix} ===== RECORDING SUMMARY =====");
            sb.AppendLine($"{Prefix} Location: {locationName}");
            sb.AppendLine($"{Prefix} Route ({route.Count} waypoints), paste into NpcRaceRoutes:");
            sb.AppendLine("new[]");
            sb.AppendLine("{");
            for (int i = 0; i < route.Count; i += 6)
            {
                var chunk = route.Skip(i).Take(6).Select(p => $"new Point({p.X}, {p.Y})");
                sb.AppendLine("    " + string.Join(", ", chunk) + ",");
            }
            sb.AppendLine("},");

            sb.AppendLine($"{Prefix} Jump zones ({jumps.Count}):");
            foreach (var (approach, landing) in jumps)
                sb.AppendLine($"{Prefix}   approach {Fmt(approach)} -> landing {Fmt(landing)}");

            // Tile lists for placing marker tiles on the two Tiled layers (order matches the pairs above,
            // which is how LoadNpcJumpZonesFromMap pairs them, but note it re-sorts top->bottom/left->right).
            sb.AppendLine($"{Prefix} NpcJumpApproach tiles: {string.Join(" ", jumps.Select(j => Fmt(j.Approach)))}");
            sb.AppendLine($"{Prefix} NpcJumpLanding tiles:  {string.Join(" ", jumps.Select(j => Fmt(j.Landing)))}");
            sb.Append($"{Prefix} =============================");

            Monitor.Log(sb.ToString(), LogLevel.Info);
        }

        /// <summary>Appends a tile to the route, skipping if it equals the previous entry. Returns true if added.</summary>
        private static bool AddToRoute(Point tile)
        {
            if (route.Count > 0 && route[route.Count - 1] == tile)
                return false;
            route.Add(tile);
            return true;
        }

        private static bool IsAirborne()
        {
            return Game1.player.mount != null
                && (Game1.player.yJumpVelocity != 0f || Game1.player.yJumpOffset != 0);
        }

        private static Point CurrentTile()
        {
            Vector2 t = Game1.player.mount?.Tile ?? Game1.player.Tile;
            return new Point((int)t.X, (int)t.Y);
        }

        private static string Fmt(Point p) => $"({p.X}, {p.Y})";
    }
}
