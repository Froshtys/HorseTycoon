using HorseTycoon;
using HorseTycoon.Menus;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Characters;
using StardewValley.Menus;
using System;

public partial class HorseSwapMenu : IClickableMenu
{
    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.HoveredIndex = -1;
        this.hoverText = "";
        this.upArrow.tryHover(x, y);
        this.downArrow.tryHover(x, y);
        this.scrollBar.tryHover(x, y);

        if (this.returnToBarnButton != null)
        {
            if (this.returnToBarnButton.containsPoint(x, y))
            {
                this.returnToBarnButton.scale = Math.Min(this.returnToBarnButton.scale + 0.05f, this.returnToBarnButton.baseScale + 0.5f);
                this.hoverText = this.returnToBarnButton.hoverText;
            }
            else
            {
                this.returnToBarnButton.scale = Math.Max(this.returnToBarnButton.scale - 0.05f, this.returnToBarnButton.baseScale);
            }
        }

        for (int i = 0; i < Math.Min(MaxVisibleItems, Animals.Count); i++)
        {
            Rectangle rowArea = new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + TopPadding + 12 + (i * RowHeight), width - 64, RowHeight);
            if (rowArea.Contains(x, y))
            {
                this.HoveredIndex = i;
                int actualIndex = i + startIndex;

                if (actualIndex < Animals.Count)
                {
                    var animal = Animals[actualIndex];
                    if (this.ActiveFarmHorse != null && animal.myID.Value == this.ActiveFarmHorse.myID.Value)
                    {
                        this.hoverText = "Return " + animal.Name + " to barn";
                    }
                }
                break;
            }
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.InputLockoutTimer > 0) return;
        base.receiveLeftClick(x, y, playSound);

        if (this.returnToBarnButton != null && this.returnToBarnButton.containsPoint(x, y))
        {
            Game1.playSound("coin");
            OnSelected(null!);
            Game1.exitActiveMenu();
            return;
        }

        if (this.upArrow.containsPoint(x, y) && this.startIndex > 0)
        {
            this.startIndex--;
            Game1.playSound("shwip");
            this.setScrollBarToCurrentIndex();
        }
        else if (this.downArrow.containsPoint(x, y) && this.startIndex < this.Animals.Count - this.MaxVisibleItems)
        {
            this.startIndex++;
            Game1.playSound("shwip");
            this.setScrollBarToCurrentIndex();
        }
        else if (this.scrollBar.containsPoint(x, y)) this.scrolling = true;
        else if (this.scrollBarRunner.Contains(x, y))
        {
            this.scrolling = true;
            this.leftClickHeld(x, y);
        }

        for (int i = 0; i < Math.Min(MaxVisibleItems, Animals.Count); i++)
        {
            int actualIndex = i + startIndex;
            if (actualIndex >= Animals.Count) break;

            Rectangle rowArea = new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + TopPadding + 12 + (i * RowHeight), width - 64, RowHeight);
            if (rowArea.Contains(x, y))
            {
                var animal = Animals[actualIndex];

                // 1. NEW TRIGGER: If the horse is a baby, play an error buzz sound and do absolutely nothing [1.6]
                if (animal.isBaby())
                {
                    Game1.playSound("cancel"); // Standard vanilla layout menu error buzz audio
                    Game1.showRedMessage("Too young to ride");
                    return;
                }

                // 2. Click Action: If the current active horse is clicked, return it
                if (this.ActiveFarmHorse != null && animal.myID.Value == this.ActiveFarmHorse.myID.Value)
                {
                    Game1.playSound("coin");
                    OnSelected(null!);
                }
                else
                {
                    Game1.playSound("coin");
                    OnSelected(animal);
                }
                Game1.exitActiveMenu();
                break;
            }
        }
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // Toggle this boolean variable to swap rendering styles easily
        bool usePixelSegments = true;

        // --- NEW: STARDEW NATIVE TITLE BANNER BLOCK ---
        string titleText = "Choose stable horse";
        int titleWidth = SpriteText.getWidthOfString(titleText);
        int titleX = this.xPositionOnScreen + (this.width / 2) - (titleWidth / 2);
        int titleY = this.yPositionOnScreen;
        SpriteText.drawStringWithScrollBackground(b, titleText, titleX, titleY);

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), this.scrollBarRunner.X, this.scrollBarRunner.Y, this.scrollBarRunner.Width, this.scrollBarRunner.Height, Color.White, 4f, false);
        this.scrollBar.draw(b);
        this.upArrow.draw(b);
        this.downArrow.draw(b);

        for (int i = 0; i < MaxVisibleItems; i++)
        {
            int actualIndex = i + startIndex;
            if (actualIndex >= Animals.Count) break;

            var animal = Animals[actualIndex];
            int relativeY = yPositionOnScreen + TopPadding + (i * RowHeight);
            int relativeX = xPositionOnScreen + 60;
            int panelX = xPositionOnScreen + 32;
            int panelWidth = width - 64;
            int panelHeight = RowHeight + 4;

            // Draw standard slot background container
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), panelX, relativeY, panelWidth, panelHeight, Color.White, 4f, false);

            if (i == this.HoveredIndex)
            {
                b.Draw(Game1.staminaRect, new Rectangle(panelX + 4, relativeY + 4, panelWidth - 8, panelHeight - 8), Color.SaddleBrown * 0.30f);
            }

            // Draw Animal Texture
            float scale = (i == this.HoveredIndex) ? 3.2f : 3.0f;
            bool isActiveHorseRow = this.ActiveFarmHorse != null && animal.myID.Value == this.ActiveFarmHorse.myID.Value;
            Horse? worldHorseEntity = (isActiveHorseRow && this.TargetStable != null) ? this.TargetStable.getStableHorse() : null;

            if (worldHorseEntity != null && worldHorseEntity.Sprite != null)
            {
                b.Draw(worldHorseEntity.Sprite.Texture, new Vector2(relativeX, relativeY), worldHorseEntity.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);
            }
            else
            {
                b.Draw(animal.Sprite.Texture, new Vector2(relativeX + 2, relativeY + 2), animal.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);
            }

            // Name and Tag Layout Engine
            string name = animal.Name;
            bool isBabyHorseRow = animal.isBaby();

            if (isActiveHorseRow)
            {
                Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 16);
                Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);

                string currentTag = "(current)";
                Vector2 tagSize = Game1.smallFont.MeasureString(currentTag);
                Vector2 tagPos = new Vector2(relativeX + 110 + (240 - tagSize.X) / 2, relativeY + 58);
                Utility.drawTextWithShadow(b, currentTag, Game1.smallFont, tagPos, Color.DarkGreen);
            }
            else if (isBabyHorseRow)
            {
                Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 16);
                Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);

                string babyTag = "(baby)";
                Vector2 tagSize = Game1.smallFont.MeasureString(babyTag);
                Vector2 tagPos = new Vector2(relativeX + 110 + (240 - tagSize.X) / 2, relativeY + 58);
                Utility.drawTextWithShadow(b, babyTag, Game1.smallFont, tagPos, Color.Gray);
            }
            else
            {
                Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 32);
                Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);
            }

            // --- DRAW TRAINING STATS BARS ---
            var stats = animal.GetHorseStats();
            if (stats != null)
            {
                int barStartX = relativeX + 345;
                int barStartY = relativeY + 16;
                int barMaxWidth = 125;
                int verticalGap = 28;

                Rectangle emptyBoxSource = new Rectangle(227, 425, 9, 9);
                Rectangle checkedBoxSource = new Rectangle(236, 425, 9, 9);
                float checkboxScale = 2.6f;
                int checkboxColumnX = barStartX + barMaxWidth + 115;

                int bar2Y = barStartY + verticalGap;
                int bar3Y = barStartY + (verticalGap * 2);

                // Render the active stat display layout choice
                if (usePixelSegments)
                {
                    int extraPadding = 32;
                    barMaxWidth = barMaxWidth + extraPadding;
                    checkboxColumnX = checkboxColumnX + extraPadding;
                    // Dark wood partition line
                    b.Draw(Game1.staminaRect, new Rectangle(relativeX + 350 - 15, relativeY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);
                    MenuDrawingHelper.DrawPixelSegments(b, barStartX, barStartY, stats.SpeedIV, stats.SpeedEV, 2f);
                    MenuDrawingHelper.DrawPixelSegments(b, barStartX, bar2Y, stats.SprintIV, stats.SprintEV, 2f);
                    MenuDrawingHelper.DrawPixelSegments(b, barStartX, bar3Y, stats.JumpIV, stats.JumpEV, 2f);
                }
                else
                {
                    // Dark wood partition line
                    b.Draw(Game1.staminaRect, new Rectangle(relativeX + 350, relativeY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);
                    this.DrawContinuousStatBar(b, barStartX, barStartY, barMaxWidth, (float)stats.TotalSpeed / 100f, Color.LimeGreen);
                    this.DrawContinuousStatBar(b, barStartX, bar2Y, barMaxWidth, (float)stats.TotalSprint / 100f, Color.LimeGreen);
                    this.DrawContinuousStatBar(b, barStartX, bar3Y, barMaxWidth, (float)stats.TotalJump / 100f, Color.LimeGreen);
                }

                // Labels and Checkboxes: Speed
                Utility.drawTextWithShadow(b, "Speed", Game1.smallFont, new Vector2(barStartX + barMaxWidth + 12, barStartY - 2), Game1.textColor, 1f);
                bool speedChecked = TrainingManager.HasTrainedSpeedToday(animal);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, barStartY + 2), speedChecked ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);

                // Labels and Checkboxes: Sprint
                Utility.drawTextWithShadow(b, "Sprint", Game1.smallFont, new Vector2(barStartX + barMaxWidth + 12, bar2Y - 2), Game1.textColor, 1f);
                bool sprintChecked = TrainingManager.HasTrainedSprintToday(animal);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, bar2Y + 2), sprintChecked ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);

                // Labels and Checkboxes: Jump
                Utility.drawTextWithShadow(b, "Jump", Game1.smallFont, new Vector2(barStartX + barMaxWidth + 12, bar3Y - 2), Game1.textColor, 1f);
                bool jumpChecked = TrainingManager.HasTrainedJumpToday(animal);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, bar3Y + 2), jumpChecked ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
            }
        }

        base.draw(b);

        if (this.returnToBarnButton != null)
        {
            this.returnToBarnButton.draw(b);
        }

        if (!string.IsNullOrEmpty(this.hoverText))
        {
            drawHoverText(b, this.hoverText, Game1.smallFont);
        }

        drawMouse(b);
    }

    /// <summary>
    /// Renders a continuous progress tray matching original design frame bounds.
    /// </summary>
    private void DrawContinuousStatBar(SpriteBatch b, int x, int y, int width, float percentage, Color barColor)
    {
        int barHeight = 24;
        Rectangle capSource = new Rectangle(323, 360, 6, 24);
        Rectangle trackSource = new Rectangle(319, 360, 1, 24);

        // Draw frame layout bounds
        b.Draw(Game1.mouseCursors, new Rectangle(x, y, 6, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 1f);
        b.Draw(Game1.mouseCursors, new Rectangle(x + 6, y, width - 12, barHeight), trackSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
        b.Draw(Game1.mouseCursors, new Rectangle(x + width - 6, y, 6, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);

        // Render inner rounded progress bar
        this.DrawRoundedProgressBar(b, x + 4, y + 5, width - 8, barHeight - 10, percentage, barColor);
    }

    private void DrawRoundedProgressBar(SpriteBatch b, int x, int y, int width, int height, float percentage, Color baseColor)
    {
        if (percentage <= 0) return;
        int fillWidth = (int)(width * percentage);
        if (fillWidth <= 0) return;

        Color darkColor = baseColor * 0.65f;

        if (fillWidth == 1)
            b.Draw(Game1.staminaRect, new Rectangle(x, y + 1, 1, height - 2), darkColor);
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