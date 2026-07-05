using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon
{
    /// <summary>
    /// Shared sprite-animation helpers for proxy/festival horses whose vanilla Horse.update
    /// animation is suppressed (EventActor horses, or horses shown while an event is up).
    /// Frames per the vanilla Horse grass-eating idle: 7 idle, 21-24 head-down.
    /// </summary>
    internal static class HorseAnimations
    {
        /// <summary>Looping grass-eating idle with a randomized standing pause.</summary>
        /// <param name="headBobPairs">How many 23/24 head-bob repetitions per loop (festival
        /// horses use 3, breeding-pen proxies 2).</param>
        public static void SetGrazing(Horse horse, int headBobPairs = 3)
        {
            if (horse.Sprite == null)
                return;

            bool flip = horse.FacingDirection == Game1.left;
            var frames = new List<FarmerSprite.AnimationFrame>
            {
                new(7, Game1.random.Next(1000, 3200), secondaryArm: false, flip),
                new(21, 100, secondaryArm: false, flip),
                new(22, 100, secondaryArm: false, flip),
            };
            for (int i = 0; i < headBobPairs; i++)
            {
                frames.Add(new(23, 400, secondaryArm: false, flip));
                frames.Add(new(24, 400, secondaryArm: false, flip));
            }
            frames.Add(new(23, 400, secondaryArm: false, flip));
            frames.Add(new(22, 100, secondaryArm: false, flip));
            frames.Add(new(21, 100, secondaryArm: false, flip));

            horse.Sprite.loop = true;
            horse.Sprite.setCurrentAnimation(frames);
        }

        /// <summary>Static standing idle (frame 7).</summary>
        public static void SetIdle(Horse horse)
        {
            if (horse.Sprite == null)
                return;

            bool flip = horse.FacingDirection == Game1.left;
            horse.Sprite.loop = true;
            horse.Sprite.setCurrentAnimation(new List<FarmerSprite.AnimationFrame>
            {
                new(7, 1000, secondaryArm: false, flip),
            });
        }
    }
}
