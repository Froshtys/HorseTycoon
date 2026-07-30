using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Characters;
using StardewValley.TokenizableStrings;

namespace HorseTycoon
{
    /// <summary>
    /// The NPC racers' horses: each rider has exactly one horse, with a fixed name, coat and tack, used
    /// at every race in every festival and every year. They're characters the dialogue can name and the
    /// player can recognise on the track, so nothing about them is randomised.
    ///
    /// The names also reach dialogue through <see cref="TokenParser"/> rather than string replacement,
    /// because attendee lines arrive two different ways at a festival — the game's own loadActors for
    /// vanilla villagers and <c>FestivalRaceManager.SpawnSpectators</c> for SVE/ES ones — and both funnel
    /// through <c>Dialogue.parseDialogueString</c>, which calls <c>TokenParser.ParseText</c>. So one
    /// registration covers every path.
    /// </summary>
    public static class NpcHorseNames
    {
        /// <summary>Names an NPC racer's horse, e.g. <c>[HorseTycoon_NpcHorseName Marnie]</c>.</summary>
        public const string NpcHorseTokenKey = "HorseTycoon_NpcHorseName";

        /// <summary>Names the horse the player brought to the festival, e.g. <c>[HorseTycoon_PlayerHorseName]</c>.</summary>
        public const string PlayerHorseTokenKey = "HorseTycoon_PlayerHorseName";

        /// <summary>Used when the player has no horse to name, so the line still reads as a sentence
        /// ("I hope you and your horse ate a good breakfast").</summary>
        private const string PlayerHorseFallback = "your horse";

        /// <summary>Used when dialogue names a rider with no horse on record (a festival definition added
        /// a rider but not their mount), so the line degrades to plain English instead of an error.</summary>
        private const string NpcHorseFallback = "their horse";

        /// <summary>One NPC racer's horse. <paramref name="Skin"/> is a coat from
        /// <c>FestivalRaceManager.AllSkins</c>; <paramref name="SaddleId"/> is a key of
        /// <see cref="HorseHelper.SaddleItemOverlays"/>.</summary>
        public record HorseIdentity(string Name, string Skin, string SaddleId);

        /// <summary>Every NPC racer's horse, keyed by rider (see <c>FestivalDefinition.NpcRiderNames</c>).</summary>
        private static readonly Dictionary<string, HorseIdentity> Horses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Marnie"] = new("Rosie", "Belgian", "HorseTycoon.SaddleWhite"),
            ["Leah"] = new("Hazel", "Chestnut", "HorseTycoon.SaddleGreen"),
            ["Abigail"] = new("Comet", "Dapple", "HorseTycoon.SaddleLavender"),
            ["Sebastian"] = new("Midnight", "BlueRoan", "HorseTycoon.SaddleRed"),
        };

        /// <summary>Every name an NPC racer's horse uses, so other horse naming (the borrowed festival
        /// horse) can avoid handing the player a duplicate.</summary>
        public static IEnumerable<string> AllRiderHorseNames => Horses.Values.Select(h => h.Name);

        /// <summary><paramref name="riderName"/>'s horse, or null if they don't have one on record.</summary>
        public static HorseIdentity? ForRiderOrNull(string? riderName) =>
            riderName != null && Horses.TryGetValue(riderName, out HorseIdentity? horse) ? horse : null;

        /// <summary>The name of <paramref name="riderName"/>'s horse.</summary>
        public static string ForRider(string? riderName) =>
            ForRiderOrNull(riderName)?.Name ?? NpcHorseFallback;

        /// <summary>Supplied by <c>FestivalRaceManager</c>: the horse the local player brought to (or
        /// borrowed at) the festival. Null outside a festival, or before one is set up.</summary>
        public static Func<string?>? FestivalHorseNameResolver;

        /// <summary>The name of the horse the player is racing, or <see cref="PlayerHorseFallback"/>.</summary>
        public static string ForPlayer(Farmer? player)
        {
            player ??= Game1.player;

            // The festival entrant, which is the horse these lines are actually about. Only meaningful
            // for the local player, since the resolver reads this screen's festival state.
            if (player == Game1.player && FestivalHorseNameResolver?.Invoke() is string entrant
                && !string.IsNullOrWhiteSpace(entrant))
                return entrant;

            // Otherwise whatever they're riding right now.
            if (player?.mount is Horse mount && !string.IsNullOrWhiteSpace(mount.Name))
                return mount.Name;

            // Otherwise the first horse they own, so the line still names a real animal out of the barn.
            string? owned = HorseHelper.GetAllBarnHorses()
                .Where(animal => !HorseHelper.IsHidden(animal))
                .Select(animal => animal.displayName ?? animal.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

            return owned ?? PlayerHorseFallback;
        }

        /// <summary>
        /// Registers both dialogue tokens. Call once from <c>ModEntry.Entry</c>: the game keeps a single
        /// static parser table for the whole process and refuses a duplicate key.
        /// </summary>
        public static void RegisterTokens()
        {
            TokenParser.RegisterParser(NpcHorseTokenKey, (string[] query, out string replacement, Random random, Farmer player) =>
            {
                replacement = ForRider(query.Length > 1 ? query[1] : null);
                return true;
            });

            TokenParser.RegisterParser(PlayerHorseTokenKey, (string[] query, out string replacement, Random random, Farmer player) =>
            {
                replacement = ForPlayer(player);
                return true;
            });
        }
    }
}
