using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace HorseTycoon.Menus
{
    public static class MenuDrawingHelper
    {
        /// <summary>
        /// Renders horse stats in discrete pixel segments
        /// </summary>
        public static void DrawPixelSegments(SpriteBatch b, int xStart, int y, int iv, int ev, float scale)
        {
            int x = xStart;
            int maxBlocks = 10;
            int totalPoints = Math.Min(iv + ev, 100);

            int totalFilledBlocks = (int)Math.Round((float)totalPoints / 10f);
            int ivFilledBlocks = (int)Math.Round((float)Math.Min(iv, 100) / 10f);

            // EXACT game dimension scaling parameters from your template
            float blockScale = scale;
            int blockWidth = (int)(8 * blockScale);
            int gap = 0;
            int verticalOffset = 6;

            for (int i = 0; i < maxBlocks; i++)
            {
                int blockX = x + (i * (blockWidth + gap));
                Vector2 renderPos = new Vector2(blockX, y + verticalOffset);

                if (i < ivFilledBlocks)
                {
                    // Dark IVs
                    b.Draw(Game1.mouseCursors, renderPos, new Rectangle(137, 338, 7, 9), Color.IndianRed, 0f, Vector2.Zero, blockScale, SpriteEffects.None, 0.8f);
                }
                else if (i < totalFilledBlocks)
                {
                    // Bright EVs
                    b.Draw(Game1.mouseCursors, renderPos, new Rectangle(137, 338, 7, 9), Color.White, 0f, Vector2.Zero, blockScale, SpriteEffects.None, 0.8f);
                }
                else if (i >= 10 - (5 - ivFilledBlocks))
                {
                    // Unreachable Potential faded
                    b.Draw(Game1.mouseCursors, renderPos, new Rectangle(129, 338, 8, 9), Color.White * 0.5f, 0f, Vector2.Zero, blockScale, SpriteEffects.None, 0.8f);
                }
                else
                {
                    // Regular Empty Slot
                    b.Draw(Game1.mouseCursors, renderPos, new Rectangle(129, 338, 8, 9), Color.White, 0f, Vector2.Zero, blockScale, SpriteEffects.None, 0.8f);
                }
            }
        }
    }
}