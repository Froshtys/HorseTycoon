using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using HorseTycoon.Models;

namespace HorseTycoon.Menus
{
    public class HorseStatsMenu : IClickableMenu
    {
        private readonly FarmAnimal Horse;
        private readonly ClickableTextureComponent BackButton;

        public HorseStatsMenu(FarmAnimal horse) : base(0, 0, 0, 0, true)
        {
            this.Horse = horse;
            this.width = 550;
            this.height = 450;

            // Correct way to center a menu in 1.6
            this.xPositionOnScreen = (Game1.uiViewport.Width / 2) - (this.width / 2);
            this.yPositionOnScreen = (Game1.uiViewport.Height / 2) - (this.height / 2);

            // Re-initialize the Back Button with the new coordinates
            this.BackButton = new ClickableTextureComponent(
                new Rectangle(this.xPositionOnScreen + this.width - 80, this.yPositionOnScreen + 20, 48, 48),
                Game1.mouseCursors,
                new Rectangle(352, 495, 12, 11),
                4f
            );
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.7f);

            // Draw standard menu background
            Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);
            int YPadding = 40;

            // Draw Horse Name
            string name = this.Horse.displayName;
            Utility.drawTextWithShadow(b, name, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + 64, this.yPositionOnScreen + 72 + YPadding), Game1.textColor);

            // Draw the Stats
            var stats = this.Horse.GetHorseStats();
            int startX = this.xPositionOnScreen + 60;
            int startY = this.yPositionOnScreen + 120 + YPadding;

            DrawStatBar(b, "Speed", startX, startY, stats.SpeedIV, stats.SpeedEV, 2);
            DrawStatBar(b, "Sprint", startX, startY + 50, stats.SprintIV, stats.SprintEV, 2);
            DrawStatBar(b, "Jump", startX, startY + 100, stats.JumpIV, stats.JumpEV, 2);

            this.BackButton.draw(b);
            this.drawMouse(b);
        }

        private void DrawStatBar(SpriteBatch b, string label, int xStart, int y, int iv, int ev, float scale)
        {
            int maxStatValue = 100;

            int barMaxWidth = (int)(125 * scale);
            int barHeight = (int)(24 * scale);
            int capWidth = (int)(6 * scale);
            int textWidth = 90;
            int x = xStart + textWidth;

            Rectangle capSource = new Rectangle(323, 360, 6, 24);
            Rectangle trackSource = new Rectangle(319, 360, 1, 24);

            Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(x - textWidth, y + (2 * scale)), Game1.textColor, 1f);

            b.Draw(Game1.mouseCursors, new Rectangle(x, y, capWidth, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 1f);
            b.Draw(Game1.mouseCursors, new Rectangle(x + capWidth, y, barMaxWidth - (capWidth * 2), barHeight), trackSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
            b.Draw(Game1.mouseCursors, new Rectangle(x + barMaxWidth - capWidth, y, capWidth, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);

            int totalPoints = Math.Min(iv + ev, maxStatValue);
            float totalPct = (float)totalPoints / maxStatValue;
            float ivPct = (float)Math.Min(iv, maxStatValue) / maxStatValue;

            // Use your exact inner padding values from the swap menu snippet:
            // x + 4 for horizontal padding, y + 5 for vertical centering, height - 10 to clear borders
            int fillX = x + capWidth - (int)(2 * scale);
            int fillY = y + capWidth - (int)(1 * scale);
            int fillWidth = barMaxWidth - capWidth - 2;
            int fillHeight = barHeight - (int)((capWidth - 2) * scale);

            // Step A: Draw total progress bar width first using the EV Color (Lime Green)
            this.DrawRoundedProgressBar(b, fillX, fillY, fillWidth, fillHeight, totalPct, Color.LimeGreen);

            // Step B: Overlay the IV portion cleanly right on top using the IV Color (Green)
            this.DrawRoundedProgressBar(b, fillX, fillY, fillWidth, fillHeight, ivPct, Color.Green);

            int targetThreshold = 50;
            if (iv < targetThreshold)
            {
                int pointDeficit = targetThreshold - iv; // Amount less than 50
                float deficitPct = (float)pointDeficit / maxStatValue;

                // Calculate exact horizontal pixel width for the deficit
                int deficitWidth = (int)(fillWidth * deficitPct);

                if (deficitWidth > 0)
                {
                    // Shift X position so the rectangle anchors on the right edge wall and flows left
                    int rightAnchorX = (fillX + fillWidth) - deficitWidth;

                    // Draw a semi-transparent black rectangle over the track
                    b.Draw(
                        Game1.staminaRect,
                        new Rectangle(rightAnchorX, fillY, deficitWidth, fillHeight),
                        Color.SaddleBrown * 0.40f // Adjust multiplier opacity here (0.45f = 45% dark tint)
                    );
                }
            }

            float notchSpacing = (float)fillWidth / 10f;
            for (int i = 1; i < 10; i++)
            {
                int notchX = fillX + (int)(i * notchSpacing);
                b.Draw(Game1.staminaRect, new Rectangle(notchX, fillY, 2, fillHeight), Color.Black * 0.20f);
            }
        }
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.BackButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                Game1.activeClickableMenu = new AnimalQueryMenu(this.Horse);
            }
            base.receiveLeftClick(x, y, playSound);
        }
        private void DrawRoundedProgressBar(SpriteBatch b, int x, int y, int width, int height, float percentage, Color baseColor)
        {
            if (percentage <= 0) return;
            int fillWidth = (int)(width * percentage);
            if (fillWidth <= 0) return;
            Color darkColor = baseColor * 0.65f;

            if (fillWidth == 1) b.Draw(Game1.staminaRect, new Rectangle(x, y + 1, 1, height - 2), darkColor);
            else if (fillWidth == 2)
            {
                b.Draw(Game1.staminaRect, new Rectangle(x, y + 1, 1, height - 2), darkColor);
                b.Draw(Game1.staminaRect, new Rectangle(x + 1, y + 1, 1, height - 2), darkColor);
            }
            else
            {
                b.Draw(Game1.staminaRect, new Rectangle(x, y + 1, 1, height - 2), darkColor);
                b.Draw(Game1.staminaRect, new Rectangle(x + 1, y, fillWidth - 2, height), baseColor);
                b.Draw(Game1.staminaRect, new Rectangle(x + fillWidth - 1, y + 1, 1, height - 2), darkColor);
            }
        }
    }
}