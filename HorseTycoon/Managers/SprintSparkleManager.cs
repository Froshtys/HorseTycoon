using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon
{
    /// <summary>
    /// Draws a sparkle trail under any horse that is sprinting: the local player's mount (all three sprint
    /// systems — the classic buff, the timing-bar minigame, and the festival race sprint) and every NPC racer
    /// the host has put into a sprint.
    ///
    /// Purely cosmetic and entirely local. Nothing here is synced: NPC sprint state is already broadcast by
    /// <see cref="FestivalRaceManager"/>, so each client spawns the same trail from its own copy of that state,
    /// and the local player's sprint is local by design.
    /// </summary>
    internal static class SprintSparkleManager
    {
        /// <summary>Ticks between sparkle spawns per sprinting horse (60 ticks/sec, so ~1 every 85ms).</summary>
        private const int SpawnIntervalTicks = 5;
        /// <summary>Sparkles spawned per horse each interval.</summary>
        private const int SparklesPerSpawn = 1;
        /// <summary>Sparkle row on TileSheets\animations — the vanilla white twinkle used by wands and forage.</summary>
        private const int SparkleRow = 354;
        private const float SparkleFrameMs = 50f;
        private const int SparkleFrames = 6;

        public static void Initialize(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }

        private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.currentLocation == null)
                return;
            if (e.Ticks % SpawnIntervalTicks != 0)
                return;

            foreach (Horse horse in GetSprintingHorses())
                SpawnSparkles(Game1.currentLocation, horse);
        }

        /// <summary>Every horse on screen that should be trailing sparkles right now.</summary>
        private static IEnumerable<Horse> GetSprintingHorses()
        {
            Horse? mount = Game1.player?.mount;
            if (mount != null && IsLocalPlayerSprinting())
                yield return mount;

            // NPC racers only exist during a festival race; the property is empty otherwise.
            foreach (Horse npcHorse in FestivalRaceManager.SprintingNpcHorses)
            {
                if (npcHorse != mount)
                    yield return npcHorse;
            }
        }

        /// <summary>True while any of the three sprint systems has the local player's mount sprinting.</summary>
        private static bool IsLocalPlayerSprinting() =>
            SprintMinigameManager.IsSprinting
            || FestivalRaceManager.IsSprinting
            || Game1.player.buffs.IsApplied("Froshty.HorseTycoon.Sprint");

        private static void SpawnSparkles(GameLocation location, Horse horse)
        {
            Rectangle bounds = horse.GetBoundingBox();

            for (int i = 0; i < SparklesPerSpawn; i++)
            {
                // Scattered across the horse's hooves, biased to the back half of its footprint so the trail
                // reads as being kicked up rather than sitting under the head.
                Vector2 position = new(
                    bounds.Center.X - 32 + Game1.random.Next(-20, 21),
                    bounds.Bottom - 40 + Game1.random.Next(-7, 8));

                location.temporarySprites.Add(new TemporaryAnimatedSprite(
                    SparkleRow,
                    SparkleFrameMs,
                    SparkleFrames,
                    1,
                    position,
                    flicker: false,
                    flipped: Game1.random.Next(2) == 0)
                {
                    scale = 0.5f + (float)Game1.random.NextDouble() * 0.25f,
                    alpha = 0.85f,
                    alphaFade = 0.015f,
                    motion = new Vector2(0f, -0.12f),
                    // Just under the horse's own sort depth so the trail sits beneath its hooves.
                    layerDepth = (bounds.Bottom - 8) / 10000f
                });
            }
        }
    }
}
