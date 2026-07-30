using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon.Patches
{
    /// <summary>
    /// Stops a remote rider's horse from galloping on the spot forever.
    ///
    /// Vanilla decides whether another player's mount animates purely from
    /// <c>rider.position.Field.IsInterpolating()</c> (<c>movementDirections</c> is local input only, so it is
    /// always empty on a replica). That flag is re-armed by EVERY incoming position delta, and
    /// <c>NetFieldBase.setInterpolationTarget</c> does not compare values first: a host that dirties its
    /// position field every tick — even writing the SAME coordinates back — makes the receiving client think
    /// the rider is permanently in motion, so the horse gallops in place until the rider dismounts. That is
    /// the class of bug fixed in JumpManager on 2026-07-09; this is the backstop on the viewing side so any
    /// future per-tick position writer (ours or another mod's) can't reproduce the same visual.
    ///
    /// The rule is simply "a horse whose rider has not actually moved for a moment is standing still", which
    /// is what vanilla itself settles on once the deltas stop, only reached sooner: vanilla's extrapolation
    /// tail runs up to 3x the interpolation window (~0.75s) after the last delta, this stops at
    /// <see cref="StillTicksBeforeStop"/>.
    /// </summary>
    internal static class RemoteRiderAnimationPatches
    {
        /// <summary>How many consecutive ticks a remote rider's position must hold still before the mount's
        /// animation is stopped. At riding speed the interpolated position moves every tick, so this only
        /// ever fires on a genuinely stationary rider; ~10 ticks (167ms) is short enough to read as an
        /// immediate stop and long enough to ride out a single stalled tick.</summary>
        private const int StillTicksBeforeStop = 10;

        /// <summary>Squared pixel distance below which the rider counts as not having moved.</summary>
        private const float StillEpsilonSquared = 0.01f;

        /// <summary>Per-rider stillness tracking, keyed by player id so it can never outlive the session's
        /// player list (a horse-keyed map would hold references to swapped-out mounts).</summary>
        private static readonly Dictionary<long, (Vector2 Position, int StillTicks)> Tracked = new();

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Horse), nameof(Horse.update), new[] { typeof(GameTime), typeof(GameLocation) }),
                postfix: new HarmonyMethod(typeof(RemoteRiderAnimationPatches), nameof(Horse_update_Postfix)));
        }

        /// <summary>Hooks the day-start cleanup for <see cref="Tracked"/> and the write watchdog.</summary>
        internal static void Initialize(IModHelper helper)
        {
            helper.Events.GameLoop.DayStarted += (_, _) => Tracked.Clear();
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => Tracked.Clear();
            helper.Events.GameLoop.SaveLoaded += (_, _) =>
            {
                Game1.player.position.fieldChangeEvent -= OnLocalPositionSet;
                Game1.player.position.fieldChangeEvent += OnLocalPositionSet;
            };
        }

        /// <summary>Tick of the last watchdog report, so a genuine offender is logged once every 10s rather
        /// than every tick.</summary>
        private static int lastWatchdogLogTick = -1000;

        /// <summary>
        /// Names whoever is dirtying the local player's position while they sit still on a horse — the
        /// host-side cause of the bug the patch above hides. Verbose-only and throttled; it fires on the
        /// local player's own position writes, so the common (actually moving) case exits on the second
        /// check. <c>fieldChangeEvent</c> is not raised by interpolation, only by real <c>Set</c> calls.
        /// </summary>
        private static void OnLocalPositionSet(StardewValley.Network.NetPosition field, Vector2 oldValue, Vector2 newValue)
        {
            if (!Logger.VerboseLogging)
                return;

            // In split-screen every screen has its own Game1.player, so make sure this write belongs to the
            // one we're inspecting before reading its mount/input state.
            Farmer player = Game1.player;
            if (player == null || !ReferenceEquals(field, player.position))
                return;
            if (player.mount == null || player.movementDirections.Count > 0)
                return;
            if (player.yJumpOffset != 0 || player.yJumpVelocity != 0f
                || player.mount.mounting.Value || player.mount.dismounting.Value)
                return;

            // A big jump is a warp/teleport, not the per-tick dribble we're hunting.
            float moved = (newValue - oldValue).LengthSquared();
            if (moved > 64f * 64f)
                return;
            if (Game1.ticks - lastWatchdogLogTick < 600)
                return;

            lastWatchdogLogTick = Game1.ticks;
            Logger.LogVerbose($"Position written while standing still on a horse ({oldValue} -> {newValue}). "
                + "Other players will see this horse gallop in place. Writer:\n"
                + new System.Diagnostics.StackTrace(true));
        }

        /// <summary>Runs after vanilla has decided this tick's animation. Only ever stops an animation, never
        /// starts one, so vanilla stays in charge of what a moving horse looks like.</summary>
        private static void Horse_update_Postfix(Horse __instance)
        {
            Farmer? rider = __instance.rider;

            // Local rider: driven by real input, and both the mod's jump code and vanilla already manage it.
            if (rider == null || rider.IsLocalPlayer)
            {
                if (rider != null)
                    Tracked.Remove(rider.UniqueMultiplayerID);
                return;
            }

            // Mid-mount/dismount and mid-jump the horse is meant to animate while the rider's ground
            // position barely changes, so leave those alone.
            if (__instance.mounting.Value || __instance.dismounting.Value
                || rider.yJumpOffset != 0 || rider.yJumpVelocity != 0f)
            {
                Tracked[rider.UniqueMultiplayerID] = (rider.Position, 0);
                return;
            }

            Vector2 position = rider.Position;
            if (!Tracked.TryGetValue(rider.UniqueMultiplayerID, out var state))
            {
                Tracked[rider.UniqueMultiplayerID] = (position, 0);
                return;
            }

            if ((position - state.Position).LengthSquared() > StillEpsilonSquared)
            {
                Tracked[rider.UniqueMultiplayerID] = (position, 0);
                return;
            }

            int stillTicks = state.StillTicks + 1;
            Tracked[rider.UniqueMultiplayerID] = (position, stillTicks);

            if (stillTicks < StillTicksBeforeStop || __instance.Sprite?.CurrentAnimation == null)
                return;

            // Mirrors vanilla's own "not moving" branch in Horse.update. Vanilla re-assigns the gallop on
            // the next tick for as long as the rider's position field keeps being dirtied, so this runs
            // every tick until the delta stream stops; only the first stop is logged.
            __instance.Sprite.StopAnimation();
            __instance.faceDirection(rider.FacingDirection);
            if (stillTicks == StillTicksBeforeStop)
                Logger.LogVerbose($"Stopped a stuck gallop animation on {rider.Name}'s horse "
                    + $"(rider stationary for {stillTicks} ticks while still reporting movement). "
                    + DescribeInterpolationState(rider));
        }

        private static readonly System.Reflection.FieldInfo? InterpolationStartTickField =
            AccessTools.Field(typeof(Netcode.NetVector2), "interpolationStartTick");

        /// <summary>
        /// Tells the two possible causes apart in the log, since they need opposite fixes.
        ///
        /// The animation flag is <c>NeedsTick</c> on the rider's position field, and vanilla clears it in
        /// <c>NetFieldBase.tickImpl</c> once the interpolation factor passes 1 (or 3 while extrapolating).
        /// So a factor above 3 with the flag still set means the field is no longer being ticked at all —
        /// <c>AbstractNetSerializable.SetParent</c> only calls <c>MarkClean</c> and never hands a pending
        /// <c>needsTick</c> to the new parent, and <c>Tick</c> only descends into children whose parent has
        /// <c>childNeedsTick</c> set, so a subtree re-parented mid-interpolation is never ticked again and
        /// the flag sticks on this client forever. Nothing can be done host-side about that one.
        ///
        /// A factor below 1 instead means deltas really are still arriving every tick, i.e. something on the
        /// host is dirtying its position while standing still (see the watchdog above, which names it).
        /// </summary>
        private static string DescribeInterpolationState(Farmer rider)
        {
            try
            {
                var field = rider.position.Field;
                if (field.Root == null || InterpolationStartTickField == null)
                    return "(interpolation state unavailable)";

                uint startTick = (uint)InterpolationStartTickField.GetValue(field)!;
                uint localTick = field.Root.Clock.GetLocalTick();
                int interpolationTicks = field.Root.Clock.InterpolationTicks;
                float factor = interpolationTicks > 0 ? (localTick - startTick) / (float)interpolationTicks : -1f;

                string cause = factor > 3f
                    ? "no deltas are arriving — stuck NeedsTick (re-parented mid-interpolation), viewer-side only"
                    : "deltas are still arriving — something on the host is dirtying its position";
                return $"interpolation factor {factor:0.00} ({localTick - startTick} ticks since the last delta, "
                    + $"window {interpolationTicks}), moving={rider.position.moving.Value}, "
                    + $"dirty={field.Dirty}: {cause}.";
            }
            catch (System.Exception ex)
            {
                return $"(could not read interpolation state: {ex.Message})";
            }
        }
    }
}
