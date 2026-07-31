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
        /// <param name="HeightAboveFeet">Pixels above the bottom of the horse's bounding box the particle
        /// spawns at. Larger values sit higher up the horse's body.</param>
        /// <param name="ScaleChange">Scale gained per tick; negative shrinks the particle as it ages.</param>
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
            float RotationChange = 0f,
            int HeightAboveFeet = 40,
            float ScaleChange = 0f);

        /// <summary>The 4-frame flame vanilla draws for torches and campfires, on LooseSprites\Cursors.</summary>
        private static readonly Rectangle[] FlameRect = { new(276, 1985, 12, 11) };

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
                    // Mixed white and blue flakes — the cycle is weighted so roughly half come out near-white
                    // and the rest chill through to the full ice blue.
                    Palette: new[]
                    {
                        Color.White,
                        new Color(170, 230, 255),
                        new Color(225, 245, 255),
                        new Color(140, 215, 255),
                        Color.White,
                        new Color(195, 238, 255),
                    },
                    TextureName: "LooseSprites\\Cursors",
                    SourceRects: SnowflakeRects,
                    RotationChange: 0.02f,
                    // Snowflakes settle on the ground, so they spawn down at the hooves rather than at
                    // the height the kicked-up sparkle trails use.
                    HeightAboveFeet: 10),

                // Ember tack: real flames — the vanilla 4-frame torch fire — licking up off the hooves,
                // rising and shrinking as they burn out.
                ["HorseTycoon.SaddleEmber"] = new(
                    AnimationRow: 0, Frames: 4, FrameMs: 70f,
                    Color: Color.White,
                    ParticlesPerSpawn: 2,
                    MinScale: 1.4f,
                    ScaleJitter: 0.9f,
                    Motion: new Vector2(0f, -0.3f),
                    AlphaFade: 0.025f,
                    // The sprite is already fire-coloured, so these only shade it: plain flame, a hotter
                    // yellow lick, and a couple of deeper oranges for variety.
                    Palette: new[]
                    {
                        Color.White,
                        new Color(255, 235, 190),
                        new Color(255, 200, 130),
                        new Color(255, 170, 100),
                    },
                    TextureName: "LooseSprites\\Cursors",
                    SourceRects: FlameRect,
                    // Between the ice trail's hooves and the sparkle trails' height: the flames read as
                    // catching at the fetlocks rather than lying flat on the ground.
                    HeightAboveFeet: 25,
                    ScaleChange: -0.012f),

                // Rainbow tack: one sparkle per spawn, each the next colour of the rainbow, fading slowly
                // enough that the whole arc trails behind the horse at once.
                ["HorseTycoon.SaddleRainbow"] = new(
                    SparkleRow, SparkleFrames, SparkleFrameMs,
                    Color.White,
                    ParticlesPerSpawn: 2,
                    MinScale: 0.65f,
                    ScaleJitter: 0.3f,
                    Motion: new Vector2(0f, -0.08f),
                    AlphaFade: 0.01f,
                    // The sparkle sprite is white and `color` multiplies it, so saturated tints read as dim.
                    // These stay high-value (no channel below ~130) to keep the twinkle bright while still
                    // reading as six distinct hues.
                    Palette: new[]
                    {
                        new Color(255, 130, 130), // red
                        new Color(255, 190, 110), // orange
                        new Color(255, 250, 150), // yellow
                        new Color(150, 255, 150), // green
                        new Color(140, 215, 255), // blue
                        new Color(215, 165, 255), // violet
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
                // reads as being kicked up rather than sitting under the head. How high up the horse the
                // trail sits is per-style — see HeightAboveFeet.
                Vector2 position = new(
                    bounds.Center.X - 32 + Game1.random.Next(-20, 21),
                    bounds.Bottom - style.HeightAboveFeet + Game1.random.Next(-7, 8));

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
                particle.scaleChange = style.ScaleChange;
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
