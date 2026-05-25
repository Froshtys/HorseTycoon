using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewModdingAPI;

namespace HorseTycoon
{
    public static class JumpLogic
    {
        private static JumpManager Manager;

        public static void Initialize(JumpManager manager)
        {
            Manager = manager;
        }

        public static void TryToJump()
        {

            // 1. Get the horse animal instance and its stats
            // Note: Assuming your horse 'mount' is linked to a 'FarmAnimal' via modData or name
            var horseAnimal = HorseHelper.GetFarmAnimalForHorse(Game1.player.mount);

            if (Manager == null || horseAnimal == null)
            {
                Manager?.Monitor.Log($"Failed to get horse stats when jumping", LogLevel.Error);
                return;
            }
            var stats = new HorseTycoon.Models.HorseStats(horseAnimal);

            // --- LOGGING ---
            Manager.Monitor.Log($"Horse: {horseAnimal.Name} | TotalJump Stat: {stats.TotalJump}", LogLevel.Debug);
            Manager.Monitor.Log($"Calculated Jump -> Distance: {stats.JumpDistance} tiles", LogLevel.Debug);

            GameLocation location = Game1.player.currentLocation;
            (int ox, int oy) = GetDirectionOffset(Game1.player.FacingDirection);
            List<bool> collisions = GetCollisions(location, ox, oy, stats.JumpDistance);
            Game1.playSound("dwop");

            Manager.PlayerJumpingWithHorse = Game1.player.isRidingHorse();
            Manager.BlockedJump = false;
            Manager.VelX = 0;
            Manager.VelY = 0;

            if (!collisions[0] && !collisions[1])
            {
                PerformBlockedJump(stats.JumpDistance);
                return;
            }

            for (int i = 1; i < collisions.Count; i++)
            {
                if (!collisions[i])
                {
                    PerformFreeJump(ox, oy, i);
                    return;
                }
            }
            PerformBlockedJump(stats.JumpDistance);
        }

        private static (int ox, int oy) GetDirectionOffset(int facingDirection)
        {
            return facingDirection switch
            {
                0 => (0, -1), // Up
                1 => (1, 0),  // Right
                2 => (0, 1),  // Down
                3 => (-1, 0), // Left
                _ => (0, 0)
            };
        }

        private static List<bool> GetCollisions(GameLocation location, int ox, int oy, int maxJumpDistance)
        {
            List<bool> collisions = new();
            for (int i = 0; i < maxJumpDistance; i++)
            {
                Rectangle box = GetBoundingBox(ox, oy, i);

                bool isColliding = location.isCollidingPosition(box, Game1.viewport, true, 0, false, Game1.player)
                                 || IsOutOfMap(location, box)
                                 || (IsOnWater(location, box) && !Manager.Helper.ModRegistry.IsLoaded("aedenthorn.Swim"));

                collisions.Add(isColliding);
            }
            return collisions;
        }

        private static Rectangle GetBoundingBox(int ox, int oy, int distance)
        {
            Rectangle box = (Game1.player.isRidingHorse() && Game1.player.mount is not null)
                ? Game1.player.mount.GetBoundingBox()
                : Game1.player.GetBoundingBox();

            box.X += ox * Game1.tileSize * distance;
            box.Y += oy * Game1.tileSize * distance;
            return box;
        }

        private static void PerformFreeJump(int ox, int oy, int distance)
        {
            float power = (float)Math.Sqrt(distance * 16);
            Manager.VelX = ox * power;
            Manager.VelY = oy * power;
            Manager.LastYJumpVelocity = 0;
            Game1.player.CanMove = false;
            PerformJump(power);

            if (Game1.player.mount != null)
            {
                TrainingManager.ProcessJump(Game1.player.mount);
            }
        }

        private static void PerformBlockedJump(int jumpHeight)
        {
            Manager.BlockedJump = true;
            PerformJump(2 + jumpHeight);

            if (Game1.player.mount != null)
            {
                TrainingManager.ProcessJump(Game1.player.mount);
            }
        }

        private static void PerformJump(float v)
        {
            Game1.player.synchronizedJump(v);
            // Link back to the manager's event handler
            Manager.SubscribeToUpdate();
        }

        public static bool IsOutOfMap(GameLocation location, Rectangle position)
        {
            return position.X < 0 ||
                   position.Y < 0 ||
                   position.X >= location.map.Layers[0].LayerWidth * Game1.tileSize ||
                   position.Y >= location.map.Layers[0].LayerHeight * Game1.tileSize;
        }

        public static bool IsOnWater(GameLocation location, Rectangle position)
        {
            if (location.waterTiles?.waterTiles != null)
            {
                int x1 = position.X / Game1.tileSize;
                int y1 = position.Y / Game1.tileSize;
                int x2 = (position.X + position.Width) / Game1.tileSize;
                int y2 = (position.Y + position.Height) / Game1.tileSize;

                if (x1 >= 0 && y1 >= 0 && x1 < location.waterTiles.waterTiles.GetLength(0) && y1 < location.waterTiles.waterTiles.GetLength(1) &&
                    x2 >= 0 && y2 >= 0 && x2 < location.waterTiles.waterTiles.GetLength(0) && y2 < location.waterTiles.waterTiles.GetLength(1))
                {
                    return location.waterTiles.waterTiles[x1, y1].isWater ||
                           location.waterTiles.waterTiles[x2, y2].isWater ||
                           location.waterTiles.waterTiles[x1, y2].isWater ||
                           location.waterTiles.waterTiles[x2, y1].isWater;
                }
            }
            return false;
        }
    }
}