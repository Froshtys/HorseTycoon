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
    /// Draws a trail under any horse that is sprinting: the local player's mount (all three sprint
    /// systems — the classic buff, the timing-bar minigame, and the festival race sprint) and every NPC racer
    /// the host has put into a sprint. The trail's look follows the horse's equipped tack — see
    /// <see cref="TrailStyles"/>.
    ///
    /// Purely cosmetic and entirely local. Nothing here is synced: NPC sprint state is already broadcast by
    /// <see cref="FestivalRaceManager"/>, so each client spawns the same trail from its own copy of that state,
    /// and the local player's sprint is local by design.
    /// </summary>
    internal static class SprintSparkleManager
    {
        /// <summary>Ticks between sparkle spawns per sprinting horse (60 ticks/sec, so ~1 every 85ms).</summary>
        private const int SpawnIntervalTicks = 5;
        /// <summary>Sparkle row on TileSheets\animations — the vanilla white twinkle used by wands and forage.</summary>
        private const int SparkleRow = 354;
        private const float SparkleFrameMs = 50f;
        private const int SparkleFrames = 6;

        /// <summary>How the trail behind one sprinting horse looks.</summary>
        /// <param name="AnimationRow">Starting index on TileSheets\animations.</param>
        /// <param name="Frames">Number of frames in that animation.</param>
        /// <param name="FrameMs">Milliseconds per frame.</param>
        /// <param name="Color">Tint applied to the particle, unless <paramref name="Palette"/> overrides it.</param>
        /// <param name="ParticlesPerSpawn">Particles emitted each spawn interval.</param>
        /// <param name="MinScale">Lower bound of the random particle scale.</param>
        /// <param name="ScaleJitter">Range added on top of <paramref name="MinScale"/>.</param>
        /// <param name="Motion">Per-tick drift; negative Y floats upward.</param>
        /// <param name="AlphaFade">Alpha lost per tick — smaller values linger longer.</param>
        /// <param name="Palette">Optional colour cycle; each successive particle takes the next entry,
        /// so a slow-fading trail shows the whole sequence at once.</param>
        /// <param name="TextureName">Optional sheet to draw from instead of TileSheets\animations. When set,
        /// <paramref name="SourceRects"/> supplies the sprite and <paramref name="AnimationRow"/> is unused.</param>
        /// <param name="SourceRects">Sprite variants on <paramref name="TextureName"/>; one is picked at random
        /// per particle.</param>
        /// <param name="RotationChange">Radians per tick the particle spins.</param>
        private readonly record struct TrailStyle(
            int AnimationRow,
            int Frames,
            float FrameMs,
            Color Color,
            int ParticlesPerSpawn,
            float MinScale,
            float ScaleJitter,
            Vector2 Motion,
            float AlphaFade,
            Color[]? Palette = null,
            string? TextureName = null,
            Rectangle[]? SourceRects = null,
            float RotationChange = 0f);

        /// <summary>The five 4x4 snowflakes vanilla snow weather is drawn from (see <c>WeatherDebris</c>).</summary>
        private static readonly Rectangle[] SnowflakeRects =
            Enumerable.Range(0, 5).Select(i => new Rectangle(391 + 4 * i, 1236, 4, 4)).ToArray();

        /// <summary>Advances every time a palette particle spawns, so colours cycle rather than repeat.</summary>
        private static int PaletteIndex;

        /// <summary>The plain white twinkle every horse gets unless its tack says otherwise.</summary>
        private static readonly TrailStyle DefaultStyle = new(
            SparkleRow, SparkleFrames, SparkleFrameMs,
            Color.White,
            ParticlesPerSpawn: 1,
            MinScale: 0.5f,
            ScaleJitter: 0.25f,
            Motion: new Vector2(0f, -0.12f),
            AlphaFade: 0.015f);

        /// <summary>Per-saddle trail overrides, keyed by unqualified saddle item ID.</summary>
        private static readonly IReadOnlyDictionary<string, TrailStyle> TrailStyles =
            new Dictionary<string, TrailStyle>
            {
                // Ice tack: pale blue snowflakes kicked off the hooves — a real crystal shape rather than
                // a tinted twinkle — tumbling slowly as they settle and linger on the ground.
                ["HorseTycoon.SaddleIce"] = new(
                    AnimationRow: 0, Frames: 1, FrameMs: 2000f,
                    Color: new Color(170, 230, 255),
                    ParticlesPerSpawn: 2,
                    MinScale: 2.5f,
                    ScaleJitter: 1.5f,
                    Motion: new Vector2(0f, 0.05f),
                    AlphaFade: 0.008f,
                    TextureName: "LooseSprites\\Cursors",
                    SourceRects: SnowflakeRects,
                    RotationChange: 0.02f),

                // Ember tack: hot sparks that rise off the hooves and burn out fast.
                ["HorseTycoon.SaddleEmber"] = new(
                    SparkleRow, SparkleFrames, FrameMs: 40f,
                    Color: new Color(255, 150, 60),
                    ParticlesPerSpawn: 2,
                    MinScale: 0.3f,
                    ScaleJitter: 0.3f,
                    Motion: new Vector2(0f, -0.35f),
                    AlphaFade: 0.025f),

                // Rainbow tack: one sparkle per spawn, each the next colour of the rainbow, fading slowly
                // enough that the whole arc trails behind the horse at once.
                ["HorseTycoon.SaddleRainbow"] = new(
                    SparkleRow, SparkleFrames, SparkleFrameMs,
                    Color.White,
                    ParticlesPerSpawn: 1,
                    MinScale: 0.5f,
                    ScaleJitter: 0.25f,
                    Motion: new Vector2(0f, -0.08f),
                    AlphaFade: 0.01f,
                    Palette: new[]
                    {
                        new Color(255, 80, 80),   // red
                        new Color(255, 165, 60),  // orange
                        new Color(255, 240, 90),  // yellow
                        new Color(110, 225, 110), // green
                        new Color(90, 170, 255),  // blue
                        new Color(190, 120, 255), // violet
                    }),
            };

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

        /// <summary>The trail style for a horse's currently equipped tack.</summary>
        private static TrailStyle GetTrailStyle(Horse horse) =>
            TrailStyles.TryGetValue(HorseHelper.GetEquippedSaddleId(horse), out TrailStyle style)
                ? style
                : DefaultStyle;

        private static void SpawnSparkles(GameLocation location, Horse horse)
        {
            Rectangle bounds = horse.GetBoundingBox();
            TrailStyle style = GetTrailStyle(horse);

            for (int i = 0; i < style.ParticlesPerSpawn; i++)
            {
                // Scattered across the horse's hooves, biased to the back half of its footprint so the trail
                // reads as being kicked up rather than sitting under the head.
                Vector2 position = new(
                    bounds.Center.X - 32 + Game1.random.Next(-20, 21),
                    bounds.Bottom - 40 + Game1.random.Next(-7, 8));

                bool flipped = Game1.random.Next(2) == 0;

                TemporaryAnimatedSprite particle =
                    style.TextureName != null && style.SourceRects is { Length: > 0 } rects
                        ? new TemporaryAnimatedSprite(
                            style.TextureName,
                            rects[Game1.random.Next(rects.Length)],
                            style.FrameMs,
                            style.Frames,
                            1,
                            position,
                            flicker: false,
                            flipped)
                        : new TemporaryAnimatedSprite(
                            style.AnimationRow,
                            style.FrameMs,
                            style.Frames,
                            1,
                            position,
                            flicker: false,
                            flipped);

                particle.scale = style.MinScale + (float)Game1.random.NextDouble() * style.ScaleJitter;
                particle.alpha = 0.85f;
                particle.alphaFade = style.AlphaFade;
                particle.color = style.Palette is { Length: > 0 } palette
                    ? palette[PaletteIndex++ % palette.Length]
                    : style.Color;
                particle.motion = style.Motion;
                if (style.RotationChange != 0f)
                {
                    // Tumbling particles start at a random angle and spin either way, so no two look alike.
                    particle.rotation = (float)(Game1.random.NextDouble() * MathHelper.TwoPi);
                    particle.rotationChange = style.RotationChange * (Game1.random.Next(2) == 0 ? 1f : -1f);
                }
                // Just under the horse's own sort depth so the trail sits beneath its hooves.
                particle.layerDepth = (bounds.Bottom - 8) / 10000f;

                location.temporarySprites.Add(particle);
            }
        }
    }
}
