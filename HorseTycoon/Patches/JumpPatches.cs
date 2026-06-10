using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon
{
    public static class JumpPatches
    {
        // This allows the patches to access your config and state
        private static JumpManager? Manager;

        public static void Initialize(JumpManager manager)
        {
            Manager = manager;
        }

        public static void Horse_draw_Prefix(Horse __instance, SpriteBatch b)
        {
            if (Manager?.HorseShadow == null || Manager.Config == null)
                return;

            Manager.GettingLocalPositionForShadow = true;

            // Draw the shadow
            b.Draw(
                Manager.HorseShadow,
                __instance.getLocalPosition(Game1.viewport) + new Vector2(__instance.Sprite.SpriteWidth * 4 / 2, __instance.GetBoundingBox().Height / 2),
                new Rectangle?(__instance.Sprite.SourceRect),
                Color.White,
                __instance.rotation,
                new Vector2((__instance.Sprite.SpriteWidth / 2) + 8, __instance.Sprite.SpriteHeight * 3f / 4f),
                Math.Max(0.2f, __instance.Scale) * 4f,
                (__instance.flip || (__instance.Sprite.CurrentAnimation != null && __instance.Sprite.CurrentAnimation[__instance.Sprite.currentAnimationIndex].flip)) ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                0
            );

            Manager.GettingLocalPositionForShadow = false;

            if (__instance.rider is not null)
            {
                bool isLocalRider = __instance.rider.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID;
                bool horseIsJumping = isLocalRider
                    ? Manager.PlayerJumpingWithHorse
                    : __instance.rider.yJumpOffset != 0;

                if (!horseIsJumping)
                {
                    __instance.yOffset = 0;
                    __instance.drawOnTop = false;
                }
                else
                {
                    __instance.yOffset = __instance.rider.yJumpOffset * 2;
                    __instance.drawOnTop = true;
                }
            }
        }

        public static void Character_getLocalPosition_Postfix(Character __instance, ref Vector2 __result)
        {
            if (Manager == null || Manager.GettingLocalPositionForShadow)
                return;

            if (__instance is Horse horse && horse.yOffset != 0)
            {
                __result.Y += horse.yOffset;
            }
        }

        public static bool Farmer_getDrawLayer_Prefix(Farmer __instance, ref float __result)
        {
            if (Manager == null)
                return true;

            if (__instance.isRidingHorse() && __instance.mount?.yOffset != 0)
            {
                __result = 0.992f;
                return false;
            }
            return true;
        }

        public static void Farmer_getMovementSpeed_Postfix(Farmer __instance, ref float __result)
        {
            // Ensure the player is on a horse and the mount exists
            if (__instance.isRidingHorse() && __instance.mount != null)
            {
                // Use your HorseHelper to link the mount back to the FarmAnimal
                var horse = HorseHelper.GetFarmAnimalForHorse(__instance.mount);
                if (horse != null)
                {
                    // Apply the speed boost calculated in HorseStats
                    var stats = horse.GetHorseStats();
                    __result += stats.SpeedBoost;
                }
            }
        }
    }
}