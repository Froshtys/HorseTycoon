using HorseTycoon;
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


        // --- NEW: STARDEW NATIVE TITLE BANNER BLOCK ---
        // Dynamically compute the center X alignment relative to the dialogue frame width
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
            int panelWidth = width - 64; // Expanded width allocation to fill the full menu space
            int panelHeight = RowHeight + 4;

            // ALWAYS draw the standard dark brown slot container box as the base layer
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), panelX, relativeY, panelWidth, panelHeight, Color.White, 4f, false);

            if (i == this.HoveredIndex)
            {
                // Draws a brown tint at 30% opacity precisely matching the panel box boundaries
                b.Draw(Game1.staminaRect, new Rectangle(panelX + 4, relativeY + 4, panelWidth - 8, panelHeight - 8), Color.SaddleBrown * 0.30f);
            }

            // Dark wood vertical partition lines
            b.Draw(Game1.staminaRect, new Rectangle(relativeX + 350, relativeY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);

            // Draw Animal Texture centered vertically
            float scale = (i == this.HoveredIndex) ? 3.2f : 3.0f;

            bool isActiveHorseRow = this.ActiveFarmHorse != null && animal.myID.Value == this.ActiveFarmHorse.myID.Value;

            // If this row represents the active stable horse, query the live world entity from the stable
            Horse? worldHorseEntity = (isActiveHorseRow && this.TargetStable != null) ? this.TargetStable.getStableHorse() : null;
            if (worldHorseEntity != null && worldHorseEntity.Sprite != null)
            {
                b.Draw(
                    worldHorseEntity.Sprite.Texture,
                    new Vector2(relativeX, relativeY),
                    worldHorseEntity.Sprite.SourceRect,
                    Color.White,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    0.88f
                );
            }
            else
            {
                b.Draw(
                    animal.Sprite.Texture,
                    new Vector2(relativeX + 2, relativeY + 2),
                    animal.Sprite.SourceRect,
                    Color.White,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    0.88f
                );
            }

            // Draw Horse Name

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
                // --- NEW: Draw Baby Tag Alignment block ---
                Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 16);
                Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);

                // Draw "(baby)" tag underneath using solid black text
                string babyTag = "(baby)";
                Vector2 tagSize = Game1.smallFont.MeasureString(babyTag);
                Vector2 tagPos = new Vector2(relativeX + 110 + (240 - tagSize.X) / 2, relativeY + 58);
                Utility.drawTextWithShadow(b, babyTag, Game1.smallFont, tagPos, Color.Gray);
            }
            else
            {
                // Standard naming center alignment block
                Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 32);
                Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);
            }

            // --- DRAW TRAINING STATS BARS WITH THE TRAINING CHECKBOX COLUMN ---
            var stats = animal.GetHorseStats();
            if (stats != null)
            {
                // Shifting barStartX to the left from +370 to +345 to leave plenty of room for Column 3
                int barStartX = relativeX + 370;
                int barStartY = relativeY + 16;

                // Compressed bar width configuration ensures font labels do not clip your borders
                int barMaxWidth = 125;
                int barHeight = 24;
                int verticalGap = 28;

                Rectangle capSource = new Rectangle(323, 360, 6, 24);
                Rectangle trackSource = new Rectangle(319, 360, 1, 24);

                // Native sprite sheet coordinates for standard Stardew Valley checkboxes
                Rectangle emptyBoxSource = new Rectangle(227, 425, 9, 9);
                Rectangle checkedBoxSource = new Rectangle(236, 425, 9, 9);
                float checkboxScale = 2.6f; // 9px * 2.6 matches your 24px high bars perfectly

                // Horizontal start position for your dedicated checkbox alignment column
                int checkboxColumnX = barStartX + barMaxWidth + 115;

                // --- 1. Speed Bar & Checkbox ---
                float speedPct = (float)stats.TotalSpeed / 100f;
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX, barStartY, 6, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 1f);
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX + 6, barStartY, barMaxWidth - 12, barHeight), trackSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX + barMaxWidth - 6, barStartY, 6, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
                this.DrawRoundedProgressBar(b, barStartX + 4, barStartY + 5, barMaxWidth - 8, barHeight - 10, speedPct, Color.LimeGreen);
                Utility.drawTextWithShadow(b, "Speed", Game1.smallFont, new Vector2(barStartX + barMaxWidth + 12, barStartY - 2), Game1.textColor, 1f);

                // Queries TrainingManager directly instead of writing modData fields to the horse
                bool speedChecked = TrainingManager.HasTrainedSpeedToday(animal);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, barStartY + 2), speedChecked ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);


                // --- 2. Sprint Bar & Checkbox ---
                float sprintPct = (float)stats.TotalSprint / 100f;
                int bar2Y = barStartY + verticalGap;
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX, bar2Y, 6, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 1f);
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX + 6, bar2Y, barMaxWidth - 12, barHeight), trackSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX + barMaxWidth - 6, bar2Y, 6, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
                this.DrawRoundedProgressBar(b, barStartX + 4, bar2Y + 5, barMaxWidth - 8, barHeight - 10, sprintPct, Color.LimeGreen); // Fixed duplicate green tint color
                Utility.drawTextWithShadow(b, "Sprint", Game1.smallFont, new Vector2(barStartX + barMaxWidth + 12, bar2Y - 2), Game1.textColor, 1f);

                bool SprintChecked = TrainingManager.HasTrainedSprintToday(animal);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, bar2Y + 2), SprintChecked ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);


                // --- 3. Jump Bar & Checkbox ---
                float jumpPct = (float)stats.TotalJump / 100f;
                int bar3Y = barStartY + (verticalGap * 2);
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX, bar3Y, 6, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 1f);
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX + 6, bar3Y, barMaxWidth - 12, barHeight), trackSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
                b.Draw(Game1.mouseCursors, new Rectangle(barStartX + barMaxWidth - 6, bar3Y, 6, barHeight), capSource, Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
                this.DrawRoundedProgressBar(b, barStartX + 4, bar3Y + 5, barMaxWidth - 8, barHeight - 10, jumpPct, Color.LimeGreen); // Fixed duplicate green tint color
                Utility.drawTextWithShadow(b, "Jump", Game1.smallFont, new Vector2(barStartX + barMaxWidth + 12, bar3Y - 2), Game1.textColor, 1f);

                bool jumpChecked = TrainingManager.HasTrainedJumpToday(animal);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, bar3Y + 2), jumpChecked ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);

                /* Leftover if an icon for training is desired 
                                Texture2D trainingIconTexture = Helper.ModContent.Load<Texture2D>("assets/training_icon.png");

                                if (trainingIconTexture != null)
                                {
                                    // Source boundaries clipping your 16x16 icon file directly from coordinate (0,0)
                                    Rectangle iconSourceRect = new Rectangle(0, 0, 16, 16);

                                    // Dynamically compute aspect multiplier factor to fit a clean 24px bounding square layout
                                    // (16px * 1.5f = 24px height bounds which aligns perfectly with your 24px high stat bars!)
                                    float iconDrawScale = 24f / trainingIconTexture.Width;

                                    // Positioning coordinates: centers the 24px icon perfectly within the middle bar row (bar2Y)
                                    Vector2 iconPos = new Vector2(column4StartX, bar2Y);

                                    b.Draw(
                                        trainingIconTexture,
                                        iconPos,
                                        iconSourceRect,
                                        Color.White,
                                        0f,
                                        Vector2.Zero,
                                        iconDrawScale,
                                        SpriteEffects.None,
                                        0.1f
                                    );
                                } */
            }
        }

        base.draw(b);
        if (this.returnToBarnButton != null)
        {
            this.returnToBarnButton.draw(b);
        }
        if (!string.IsNullOrEmpty(this.hoverText)) drawHoverText(b, this.hoverText, Game1.smallFont);
        drawMouse(b);
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