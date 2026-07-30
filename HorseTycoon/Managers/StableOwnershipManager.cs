using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;

namespace HorseTycoon
{
    /// <summary>
    /// Per-player stable ownership, so the vanilla Horse Flute summons each player's own horse.
    /// <para>Ownership is a property of the STABLE, never of a horse or a barn FarmAnimal: whichever horse
    /// currently lives in your stable is the one your flute calls. Vanilla resolves a flute in
    /// <c>FarmerTeam.OnRequestHorseWarp</c> by looking for the first stable whose horse's <c>ownerId</c>
    /// is the requester, and <c>Stable.updateHorseOwnership</c> copies <c>Stable.owner</c> onto that horse
    /// — so owning the building is the whole mechanism.</para>
    /// <para>Vanilla allows one owned stable per player: <c>Game1.UpdateHorseOwnership</c> zeroes the owner
    /// of any extra stable each morning. We keep that rule rather than fight it; extra stables are still
    /// walk-up rideable, just not summonable.</para>
    /// </summary>
    public static class StableOwnershipManager
    {
        private const string MsgClaimStable = "HorseTycoon.ClaimStable";

        /// <param name="StableId">The stable's <see cref="Building.id"/> GUID as a string.</param>
        /// <param name="OnlyIfUnowned">True for the silent claim-by-mounting path, which must not move
        /// ownership off a stable the player already owns.</param>
        private record ClaimStableMessage(string StableId, long PlayerId, bool OnlyIfUnowned);

        /// <summary>Set once the host has released its duplicate stables on an existing save.</summary>
        private const string MigratedKey = "Froshty.HorseTycoon/StableOwnershipMigrated";

        private static IModHelper Helper = null!;
        private static IMonitor Monitor = null!;

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            Helper = helper;
            Monitor = monitor;

            helper.Events.Multiplayer.ModMessageReceived += OnMessageReceived;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;

            helper.ConsoleCommands.Add(
                "ht_stable_owners",
                "Lists every stable, the horse in it, and who owns it (for debugging Horse Flute summoning).",
                (_, _) => LogStableOwners());
        }

        // ---------------------------------------------------------------------
        // Lookups
        // ---------------------------------------------------------------------

        /// <summary>Every non-tractor stable anywhere buildings can exist. Deliberately not
        /// <c>Game1.getFarm().buildings</c>: vanilla's flute and its ownership passes both use
        /// <see cref="Utility.ForEachBuilding"/>, so an Island stable counts against the one-per-player
        /// rule whether we look at it or not.</summary>
        public static List<Stable> AllStables()
        {
            var stables = new List<Stable>();
            Utility.ForEachBuilding((Stable stable) =>
            {
                if (!stable.IsTractorGarage())
                    stables.Add(stable);
                return true;
            });
            return stables;
        }

        /// <summary>The stable this player owns, or null. Vanilla guarantees at most one.</summary>
        public static Stable? GetOwnedStable(long playerId) =>
            playerId == 0 ? null : AllStables().FirstOrDefault(s => s.owner.Value == playerId);

        /// <summary>What this player's <see cref="Farmer.horseName"/> should be: the name of the horse in
        /// the stable they own, or null when they own no stable or it stands empty.
        /// <para>Null matters — <c>Utility.GetHorseWarpRestrictionsForFarmer</c> only reports "you don't own
        /// a horse" for null, so an empty string would make the flute silently do nothing instead.</para></summary>
        private static string? GetDesiredHorseName(Farmer who)
        {
            Stable? stable = GetOwnedStable(who.UniqueMultiplayerID);
            if (stable == null || stable.HorseId == Guid.Empty)
                return null;

            string? name = stable.getStableHorse()?.Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }

        // ---------------------------------------------------------------------
        // horseName upkeep
        // ---------------------------------------------------------------------

        /// <summary>Keeps the local player's <c>horseName</c> equal to the horse in the stable they own.
        /// <para>This is vanilla bookkeeping, not our record of ownership — but keeping it true means
        /// <c>Game1.UpdateHorseOwnership</c>'s first pass claims each player's stable on an exact match, so
        /// its later passes (which can hand a stable to a different player whose horseName happens to match
        /// a horse's name) never get the chance to misfire.</para></summary>
        public static void SyncHorseNameForLocalPlayer()
        {
            string? desired = GetDesiredHorseName(Game1.player);
            if (Game1.player.horseName.Value == desired)
                return;

            Game1.player.horseName.Value = desired;
            Logger.LogVerbose($"horseName for {Game1.player.Name} set to '{desired ?? "(none)"}'.");
        }

        /// <summary>Host-side equivalent for the stable's owner, including offline farmhands (whose client
        /// isn't around to run the local sync).</summary>
        public static void SyncHorseNameForStableOwner(Stable? stable)
        {
            if (!Context.IsMainPlayer || stable == null || stable.owner.Value == 0)
                return;

            Farmer? owner = Game1.GetPlayer(stable.owner.Value);
            if (owner == null)
                return;

            string? desired = GetDesiredHorseName(owner);
            if (owner.horseName.Value == desired)
                return;

            owner.horseName.Value = desired;
            Logger.LogVerbose($"horseName for {owner.Name} set to '{desired ?? "(none)"}'.");
        }

        /// <summary>Host-only day-start sweep: re-syncs every owner's horseName, covering any path that
        /// changed a stable's horse without telling us.</summary>
        private static void RepairStableOwnership()
        {
            foreach (Stable stable in AllStables())
                SyncHorseNameForStableOwner(stable);
        }

        // ---------------------------------------------------------------------
        // Claiming
        // ---------------------------------------------------------------------

        /// <summary>Client entry point. Applies directly on the host, otherwise asks the host to do it.</summary>
        /// <param name="onlyIfUnowned">Silent claim-by-mounting: skip when this player already owns a stable.</param>
        public static void RequestStableClaim(Stable stable, bool onlyIfUnowned)
        {
            if (stable == null || stable.IsTractorGarage())
                return;
            if (onlyIfUnowned && GetOwnedStable(Game1.player.UniqueMultiplayerID) != null)
                return;

            if (Context.IsMainPlayer)
            {
                ClaimStable(Game1.player.UniqueMultiplayerID, stable);
                return;
            }

            Helper.Multiplayer.SendMessage(
                new ClaimStableMessage(stable.id.Value.ToString(), Game1.player.UniqueMultiplayerID, onlyIfUnowned),
                MsgClaimStable,
                modIDs: new[] { Helper.ModRegistry.ModID });

            // Optimistic: the flute's client-side gate reads our own horseName, so don't make the player
            // wait for the owner field to round-trip before it works.
            Game1.player.horseName.Value = stable.getStableHorse()?.Name;
        }

        /// <summary>Host-side apply. Idempotent, and enforces one owned stable per player by releasing any
        /// other stable the claimant owns.</summary>
        public static bool ClaimStable(long playerId, Stable stable)
        {
            if (!Context.IsMainPlayer || stable == null || stable.IsTractorGarage() || stable.isUnderConstruction())
                return false;

            Farmer? claimant = Game1.GetPlayer(playerId);
            if (claimant == null)
                return false;
            if (stable.owner.Value == playerId)
                return true;

            foreach (Stable other in AllStables())
            {
                if (other.owner.Value != playerId || other.id.Value == stable.id.Value)
                    continue;

                other.owner.Value = 0;
                other.updateHorseOwnership();
                Logger.LogVerbose($"Released {claimant.Name}'s previous stable {other.id.Value}.");
            }

            stable.owner.Value = playerId;
            stable.updateHorseOwnership();
            SyncHorseNameForStableOwner(stable);

            Monitor.Log(
                $"{claimant.Name} now owns stable {stable.id.Value} (horse: {stable.getStableHorse()?.Name ?? "empty"}).",
                LogLevel.Info);
            return true;
        }

        private static void OnMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (!Context.IsMainPlayer || e.Type != MsgClaimStable || e.FromModID != Helper.ModRegistry.ModID)
                return;

            ClaimStableMessage msg = e.ReadAs<ClaimStableMessage>();
            Stable? stable = AllStables().FirstOrDefault(s => s.id.Value.ToString() == msg.StableId);
            if (stable == null)
                return;

            // Re-checked host-side so two clients auto-claiming on the same tick can't both win.
            if (msg.OnlyIfUnowned && GetOwnedStable(msg.PlayerId) != null)
                return;

            ClaimStable(msg.PlayerId, stable);
        }

        // ---------------------------------------------------------------------
        // Events, migration, debugging
        // ---------------------------------------------------------------------

        private static void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (Context.IsMainPlayer)
                RepairStableOwnership();

            SyncHorseNameForLocalPlayer();
        }

        private static void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (Context.IsMainPlayer)
                MigrateHostStables();

            SyncHorseNameForLocalPlayer();
        }

        /// <summary>One-shot: on a save that predates per-player ownership every stable belongs to the host,
        /// so keep one and release the rest for farmhands to claim. Vanilla would eventually do this itself
        /// (its duplicate pass), but which stable it keeps depends on opaque iteration order.</summary>
        private static void MigrateHostStables()
        {
            Farm farm = Game1.getFarm();
            if (farm.modData.ContainsKey(MigratedKey))
                return;
            farm.modData[MigratedKey] = "true";

            long hostId = Game1.player.UniqueMultiplayerID;
            List<Stable> hostOwned = AllStables().Where(s => s.owner.Value == hostId).ToList();
            if (hostOwned.Count <= 1)
                return;

            // Prefer the stable whose horse the host is already recorded as owning.
            Stable keep = hostOwned.FirstOrDefault(s => s.getStableHorse()?.Name == Game1.player.horseName.Value)
                ?? hostOwned[0];

            foreach (Stable stable in hostOwned)
            {
                if (stable.id.Value == keep.id.Value)
                    continue;

                stable.owner.Value = 0;
                stable.updateHorseOwnership();
            }

            Monitor.Log(
                $"Stable ownership migrated: kept '{keep.getStableHorse()?.Name ?? "empty"}' for {Game1.player.Name}, "
                + $"released {hostOwned.Count - 1} stable(s) for other players to claim.",
                LogLevel.Info);
        }

        private static void LogStableOwners()
        {
            if (!Context.IsWorldReady)
            {
                Monitor.Log("Load a save first.", LogLevel.Warn);
                return;
            }

            List<Stable> stables = AllStables();
            Monitor.Log($"{stables.Count} stable(s):", LogLevel.Info);
            foreach (Stable stable in stables)
            {
                Farmer? owner = stable.owner.Value == 0 ? null : Game1.GetPlayer(stable.owner.Value);
                Monitor.Log(
                    $"  {stable.id.Value} @ ({stable.tileX.Value},{stable.tileY.Value}) "
                    + $"| horse: {stable.getStableHorse()?.Name ?? "(none)"} "
                    + $"| owner: {owner?.Name ?? (stable.owner.Value == 0 ? "(unclaimed)" : $"(offline {stable.owner.Value})")} "
                    + $"| their horseName: {owner?.horseName.Value ?? "(null)"}",
                    LogLevel.Info);
            }

            foreach (Farmer farmer in Game1.getAllFarmers())
            {
                Stable? owned = GetOwnedStable(farmer.UniqueMultiplayerID);
                Monitor.Log(
                    $"  {farmer.Name}: owns {(owned != null ? owned.id.Value.ToString() : "(nothing)")}, "
                    + $"horseName '{farmer.horseName.Value ?? "(null)"}'",
                    LogLevel.Info);
            }
        }
    }
}
