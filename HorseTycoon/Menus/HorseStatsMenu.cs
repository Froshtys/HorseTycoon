using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using HorseTycoon;
using HorseTycoon.Models;
using System.Dynamic;

namespace HorseTycoon.Menus
{
    public class HorseStatsMenu : IClickableMenu
    {
        private readonly FarmAnimal Horse;
        private readonly ClickableTextureComponent BackButton;
        private bool usePixelSegments = true;

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

            Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);

            // Title scroll banner with horse name
            string title = this.Horse.displayName;
            int titleWidth = SpriteText.getWidthOfString(title);
            int titleX = this.xPositionOnScreen + (this.width / 2) - (titleWidth / 2);
            SpriteText.drawStringWithScrollBackground(b, title, titleX, this.yPositionOnScreen);

            // Layout anchors
            int startX = this.xPositionOnScreen + 60;
            int textWidth = 90;
            int checkboxX = startX + textWidth + 280;
            int keyY = this.yPositionOnScreen + 114;
            int startY = keyY + 50;
            var stats = this.Horse.GetHorseStats();

            if (this.usePixelSegments)
            {
                int gemWidth = 26;
                b.Draw(Game1.mouseCursors, new Vector2(startX, keyY), new Rectangle(137, 338, 7, 9), Color.IndianRed, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.8f);
                Utility.drawTextWithShadow(b, "Genetic", Game1.smallFont, new Vector2(startX + gemWidth, keyY), Game1.textColor);
                b.Draw(Game1.mouseCursors, new Vector2(startX + gemWidth + textWidth + 10, keyY), new Rectangle(137, 338, 7, 9), Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.8f);
                Utility.drawTextWithShadow(b, "Trained", Game1.smallFont, new Vector2(startX + gemWidth + textWidth + gemWidth + 10, keyY), Game1.textColor);

                // Header above checkboxes
                Utility.drawTextWithShadow(b, "Daily", Game1.smallFont, new Vector2(checkboxX - 12, keyY), Game1.textColor);

                // Stat bars with labels
                MenuDrawingHelper.DrawPixelSegments(b, startX + textWidth, startY - 3, stats.SpeedIV, stats.SpeedEV, 3f);
                Utility.drawTextWithShadow(b, "Speed", Game1.smallFont, new Vector2(startX, startY - 2), Game1.textColor, 1f);
                MenuDrawingHelper.DrawPixelSegments(b, startX + textWidth, startY - 3 + 50, stats.SprintIV, stats.SprintEV, 3f);
                Utility.drawTextWithShadow(b, "Sprint", Game1.smallFont, new Vector2(startX, startY + 50 - 2), Game1.textColor, 1f);
                MenuDrawingHelper.DrawPixelSegments(b, startX + textWidth, startY - 3 + 100, stats.JumpIV, stats.JumpEV, 3f);
                Utility.drawTextWithShadow(b, "Jump", Game1.smallFont, new Vector2(startX, startY + 100 - 2), Game1.textColor, 1f);

                // Training checkboxes — 10 segments × 24px = 240px wide, 20px gap
                Rectangle emptyBoxSource = new Rectangle(227, 425, 9, 9);
                Rectangle checkedBoxSource = new Rectangle(236, 425, 9, 9);
                float checkboxScale = 3f;

                bool speedTrained = TrainingManager.HasTrainedSpeedToday(this.Horse);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxX, startY + 2), speedTrained ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);

                bool sprintTrained = TrainingManager.HasTrainedSprintToday(this.Horse);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxX, startY + 50 + 2), sprintTrained ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);

                bool jumpTrained = TrainingManager.HasTrainedJumpToday(this.Horse);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxX, startY + 100 + 2), jumpTrained ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
            }
            else
            {
                this.DrawStatBar(b, "Speed", startX, startY, stats.SpeedIV, stats.SpeedEV, 2);
                this.DrawStatBar(b, "Sprint", startX, startY + 50, stats.SprintIV, stats.SprintEV, 2);
                this.DrawStatBar(b, "Jump", startX, startY + 100, stats.JumpIV, stats.JumpEV, 2);
            }

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

            int fillX = x + capWidth - (int)(2 * scale);
            int fillY = y + capWidth - (int)(1 * scale);
            int fillWidth = barMaxWidth - capWidth - 2;
            int fillHeight = barHeight - (int)((capWidth - 2) * scale);

            this.DrawRoundedProgressBar(b, fillX, fillY, fillWidth, fillHeight, totalPct, Color.LimeGreen);
            this.DrawRoundedProgressBar(b, fillX, fillY, fillWidth, fillHeight, ivPct, Color.Green);

            int targetThreshold = 50;
            if (iv < targetThreshold)
            {
                int pointDeficit = targetThreshold - iv;
                float deficitPct = (float)pointDeficit / maxStatValue;
                int deficitWidth = (int)(fillWidth * deficitPct);

                if (deficitWidth > 0)
                {
                    int rightAnchorX = (fillX + fillWidth) - deficitWidth;
                    b.Draw(Game1.staminaRect, new Rectangle(rightAnchorX, fillY, deficitWidth, fillHeight), Color.SaddleBrown * 0.45f);
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
                return;
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