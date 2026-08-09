using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StardewValley;

namespace HorseTycoon
{
    /// <summary>
    /// The farm's festival race log: one entry per race anyone from the farm has run, naming the winner
    /// and where each player finished. Read by the Horse Computer's Race results tab.
    /// <para>Records live in the Farm's modData, which is net-synced and saved, so a farmhand's computer
    /// shows the same history the host's does. Only the host writes — it's the only screen that knows the
    /// full finish order (see <see cref="FestivalRaceManager"/>) — and the sync carries it to everyone.</para>
    /// </summary>
    public static class RaceHistoryManager
    {
        private const string HistoryKey = "Froshty.HorseTycoon/RaceHistory";

        // Flat delimited storage rather than JSON: the log is a handful of short fields per race, and
        // this keeps it readable in a save file the same way the mod's other modData values are.
        private const char RecordSeparator = ';';
        private const char FieldSeparator = '|';
        private const char PlacementSeparator = ',';
        private const char PlacementPair = ':';

        /// <summary>One finished race: who won, how big the field was, and every player's placement
        /// (1-based, keyed by UniqueMultiplayerID). Players who didn't run simply aren't in the map.</summary>
        public record RaceRecord(string Season, int Year, string FestivalName, string WinnerName, int TotalRacers, IReadOnlyDictionary<long, int> Placements)
        {
            /// <summary>Calendar order within a year, for sorting the log.</summary>
            public int SeasonOrder => Season?.ToLowerInvariant() switch
            {
                "spring" => 0,
                "summer" => 1,
                "fall" => 2,
                "winter" => 3,
                _ => 4
            };

            /// <summary>This player's 1-based placement, or null when they didn't race.</summary>
            public int? PlacementFor(long farmerId) =>
                this.Placements.TryGetValue(farmerId, out int placement) ? placement : null;
        }

        /// <summary>Every recorded race, newest first.</summary>
        public static List<RaceRecord> GetHistory()
        {
            if (!Game1.getFarm().modData.TryGetValue(HistoryKey, out string raw) || string.IsNullOrWhiteSpace(raw))
                return new List<RaceRecord>();

            return raw.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(Parse)
                .Where(record => record != null)
                .Select(record => record!)
                .OrderByDescending(record => record.Year)
                .ThenByDescending(record => record.SeasonOrder)
                .ToList();
        }

        /// <summary>
        /// Logs a finished race. Host only — a farmhand's write to the Farm's modData wouldn't reach
        /// the other players. Re-running the same festival in the same year (a restarted race) updates
        /// nothing: the first ceremony is the result of record.
        /// </summary>
        /// <param name="placements">Player id → 1-based finishing position, for the human racers only.</param>
        public static void RecordRace(string season, int year, string festivalName, string winnerName, int totalRacers, IReadOnlyDictionary<long, int> placements)
        {
            if (!Game1.IsMasterGame)
                return;

            List<RaceRecord> history = GetHistory();
            if (history.Any(record => record.Year == year && string.Equals(record.Season, season, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogVerbose($"Race history already holds {season} year {year}; not recording again.");
                return;
            }

            history.Add(new RaceRecord(season, year, festivalName, winnerName, totalRacers, new Dictionary<long, int>(placements)));
            Save(history);
            Logger.LogVerbose($"Race history: recorded {season} year {year} ('{festivalName}'), winner '{winnerName}', {placements.Count} player placement(s) of {totalRacers} racers.");
        }

        private static void Save(IEnumerable<RaceRecord> history)
        {
            string raw = string.Join(RecordSeparator, history.Select(Serialize));
            Game1.getFarm().modData[HistoryKey] = raw;
        }

        private static string Serialize(RaceRecord record)
        {
            string placements = string.Join(PlacementSeparator,
                record.Placements.Select(pair => $"{pair.Key}{PlacementPair}{pair.Value}"));

            return string.Join(FieldSeparator,
                Sanitize(record.Season),
                record.Year.ToString(CultureInfo.InvariantCulture),
                Sanitize(record.FestivalName),
                Sanitize(record.WinnerName),
                record.TotalRacers.ToString(CultureInfo.InvariantCulture),
                placements);
        }

        private static RaceRecord? Parse(string raw)
        {
            string[] fields = raw.Split(FieldSeparator);
            if (fields.Length < 6 || !int.TryParse(fields[1], out int year) || !int.TryParse(fields[4], out int totalRacers))
            {
                Logger.LogVerbose($"Race history: skipping unreadable entry '{raw}'.");
                return null;
            }

            Dictionary<long, int> placements = new();
            foreach (string pair in fields[5].Split(PlacementSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split(PlacementPair);
                if (parts.Length == 2 && long.TryParse(parts[0], out long farmerId) && int.TryParse(parts[1], out int placement))
                    placements[farmerId] = placement;
            }

            return new RaceRecord(fields[0], year, fields[2], fields[3], totalRacers, placements);
        }

        /// <summary>Strips the storage delimiters from a name a player could have typed anything into,
        /// so one odd farm name can't corrupt the rest of the log.</summary>
        private static string Sanitize(string? value) =>
            (value ?? "").Replace(RecordSeparator, ' ').Replace(FieldSeparator, ' ').Trim();

        /// <summary>"1st", "2nd", "3rd", "4th", ... for a placement.</summary>
        public static string Ordinal(int placement)
        {
            if (placement % 100 is >= 11 and <= 13)
                return placement + "th";

            return (placement % 10) switch
            {
                1 => placement + "st",
                2 => placement + "nd",
                3 => placement + "rd",
                _ => placement + "th"
            };
        }
    }
}
