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

        /// <summary>Static standing frame for a specific facing. The horse sheet is 7 frames per row:
        /// row 0 down, row 1 right (flipped for left), row 2 up. The standing frame is the first of
        /// the matching row. Assigned as a one-frame animation rather than by clearing CurrentAnimation:
        /// AnimatedSprite.faceDirection is a no-op while an animation is set, and callers that top up
        /// missing animations would otherwise refill this with the side-on idle.</summary>
        public static void SetStanding(Horse horse, int direction)
        {
            if (horse.Sprite == null)
                return;

            bool flip = direction == Game1.left;
            int frame = StandingFrame(direction);
            horse.Sprite.loop = true;
            horse.Sprite.setCurrentAnimation(new List<FarmerSprite.AnimationFrame>
            {
                new(frame, 1000, secondaryArm: false, flip),
            });
        }

        /// <summary>Whether the horse is already showing <see cref="SetStanding"/> for this direction.</summary>
        public static bool IsStanding(Horse horse, int direction)
        {
            var anim = horse.Sprite?.CurrentAnimation;
            return anim != null && anim.Count == 1 && anim[0].frame == StandingFrame(direction);
        }

        private static int StandingFrame(int direction) => direction switch
        {
            Game1.up => 14,
            Game1.right or Game1.left => 7,
            _ => 0, // down
        };

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
