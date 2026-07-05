using HorseTycoon;
using HorseTycoon.Menus;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using System;
using System.Collections.Generic;

/// <summary>
/// Generic horse picker in the style of <see cref="HorseSwapMenu"/>: lists the given horses with
/// their stat segments and invokes a callback with the clicked one. Used by the festival Stud Shop
/// to choose which of the horses the player brought should be bred. Babies and pregnant mares are
/// shown but can't be selected. Closing the menu cancels (callback never fires).
/// </summary>
public class HorseSelectMenu : IClickableMenu
{
    private readonly string Title;
    private readonly List<FarmAnimal> Animals;
    private readonly Action<FarmAnimal> OnSelected;

    private readonly int RowHeight = 112;
    private readonly int TopPadding = 96;
    private readonly int MaxVisibleItems = 4;
    private int startIndex = 0;
    private int HoveredIndex = -1;

    private ClickableTextureComponent upArrow;
    private ClickableTextureComponent downArrow;

    private ClickableTextureComponent scrollBar;
    private Rectangle scrollBarRunner;
    private bool scrolling = false;

    private string hoverText = "";
    private int InputLockoutTimer = 150;

    public HorseSelectMenu(string title, List<FarmAnimal> animals, Action<FarmAnimal> onSelected)
        : base(Game1.uiViewport.Width / 2 - 375, Game1.uiViewport.Height / 2 - 290, 770, 580, showUpperRightCloseButton: true)
    {
        this.Title = title;
        this.Animals = animals;
        this.OnSelected = onSelected;

        int rightScrollEdgeX = this.xPositionOnScreen + this.width + 16;
        this.upArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + TopPadding, 44, 48), Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
        this.downArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + this.height - 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);

        this.scrollBar = new ClickableTextureComponent(new Rectangle(this.upArrow.bounds.X + 12, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, 24, 40), Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);
        this.scrollBarRunner = new Rectangle(this.scrollBar.bounds.X, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, this.scrollBar.bounds.Width, this.downArrow.bounds.Y - this.upArrow.bounds.Y - this.upArrow.bounds.Height - 8);

        this.setScrollBarToCurrentIndex();
    }

    private void setScrollBarToCurrentIndex()
    {
        if (this.Animals.Count > 0)
        {
            this.scrollBar.bounds.Y = this.scrollBarRunner.Y + (this.scrollBarRunner.Height - this.scrollBar.bounds.Height) * this.startIndex / Math.Max(1, this.Animals.Count - this.MaxVisibleItems);
            if (this.startIndex == this.Animals.Count - this.MaxVisibleItems && this.Animals.Count > this.MaxVisibleItems)
            {
                this.scrollBar.bounds.Y = this.downArrow.bounds.Y - this.scrollBar.bounds.Height - 4;
            }
        }
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (this.InputLockoutTimer > 0)
            this.InputLockoutTimer -= time.ElapsedGameTime.Milliseconds;
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        if (direction > 0 && this.startIndex > 0)
        {
            this.startIndex--;
            Game1.playSound("shiny4");
            this.setScrollBarToCurrentIndex();
        }
        else if (direction < 0 && this.startIndex < this.Animals.Count - this.MaxVisibleItems)
        {
            this.startIndex++;
            Game1.playSound("shiny4");
            this.setScrollBarToCurrentIndex();
        }
    }

    public override void leftClickHeld(int x, int y)
    {
        base.leftClickHeld(x, y);
        if (this.scrolling)
        {
            int oldY = this.scrollBar.bounds.Y;
            this.scrollBar.bounds.Y = Math.Max(this.scrollBarRunner.Y, Math.Min(this.scrollBarRunner.Y + this.scrollBarRunner.Height - this.scrollBar.bounds.Height, y));
            float percentage = (float)(y - this.scrollBarRunner.Y) / (float)this.scrollBarRunner.Height;
            this.startIndex = Math.Max(0, Math.Min(this.Animals.Count - this.MaxVisibleItems, (int)Math.Round(percentage * (float)(this.Animals.Count - this.MaxVisibleItems))));
            this.setScrollBarToCurrentIndex();
            if (oldY != this.scrollBar.bounds.Y) Game1.playSound("shiny4");
        }
    }

    public override void releaseLeftClick(int x, int y)
    {
        base.releaseLeftClick(x, y);
        this.scrolling = false;
    }

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.HoveredIndex = -1;
        this.hoverText = "";
        this.upArrow.tryHover(x, y);
        this.downArrow.tryHover(x, y);
        this.scrollBar.tryHover(x, y);

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
                    if (!animal.isBaby() && !HorseHelper.IsPregnant(animal))
                        this.hoverText = "Choose " + animal.Name;
                }
                break;
            }
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.InputLockoutTimer > 0) return;
        base.receiveLeftClick(x, y, playSound);

        if (this.upArrow.containsPoint(x, y) && this.startIndex > 0)
        {
            this.startIndex--;
            Game1.playSound("shwip");
            this.setScrollBarToCurrentIndex();
            return;
        }
        else if (this.downArrow.containsPoint(x, y) && this.startIndex < this.Animals.Count - this.MaxVisibleItems)
        {
            this.startIndex++;
            Game1.playSound("shwip");
            this.setScrollBarToCurrentIndex();
            return;
        }
        else if (this.scrollBar.containsPoint(x, y))
        {
            this.scrolling = true;
            return;
        }
        else if (this.scrollBarRunner.Contains(x, y))
        {
            this.scrolling = true;
            this.leftClickHeld(x, y);
            return;
        }

        for (int i = 0; i < Math.Min(MaxVisibleItems, Animals.Count); i++)
        {
            int actualIndex = i + startIndex;
            if (actualIndex >= Animals.Count) break;

            Rectangle rowArea = new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + TopPadding + 12 + (i * RowHeight), width - 64, RowHeight);
            if (rowArea.Contains(x, y))
            {
                var animal = Animals[actualIndex];

                if (animal.isBaby())
                {
                    Game1.playSound("cancel");
                    Game1.showRedMessage("Too young to breed");
                    return;
                }

                if (HorseHelper.IsPregnant(animal))
                {
                    Game1.playSound("cancel");
                    Game1.showRedMessage("Already pregnant");
                    return;
                }

                Game1.playSound("coin");
                Game1.exitActiveMenu();
                this.OnSelected(animal);
                break;
            }
        }
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        int titleWidth = SpriteText.getWidthOfString(this.Title);
        int titleX = this.xPositionOnScreen + (this.width / 2) - (titleWidth / 2);
        SpriteText.drawStringWithScrollBackground(b, this.Title, titleX, this.yPositionOnScreen);

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
            b.Draw(animal.Sprite.Texture, new Vector2(relativeX + 2, relativeY + 2), animal.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            // Name and Tag Layout Engine
            string name = animal.Name;
            bool isBabyHorseRow = animal.isBaby();
            bool isPregnantHorseRow = !isBabyHorseRow && HorseHelper.IsPregnant(animal);

            if (isBabyHorseRow || isPregnantHorseRow)
            {
                Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 16);
                Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);

                string tag = isBabyHorseRow ? "(baby)" : "(pregnant)";
                Color tagColor = isBabyHorseRow ? Color.Gray : Color.MediumVioletRed;
                Vector2 tagSize = Game1.smallFont.MeasureString(tag);
                Vector2 tagPos = new Vector2(relativeX + 110 + (240 - tagSize.X) / 2, relativeY + 58);
                Utility.drawTextWithShadow(b, tag, Game1.smallFont, tagPos, tagColor);
            }
            else
            {
                Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 32);
                Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);
            }

            // --- DRAW STAT BARS (no training checkboxes — just the segments) ---
            var stats = animal.GetHorseStats();
            if (stats != null)
            {
                int barStartX = relativeX + 345;
                int barStartY = relativeY + 16;
                int verticalGap = 28;
                int bar2Y = barStartY + verticalGap;
                int bar3Y = barStartY + (verticalGap * 2);
                int labelX = barStartX + 125 + 32 + 12;

                // Dark wood partition line
                b.Draw(Game1.staminaRect, new Rectangle(relativeX + 350 - 15, relativeY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);
                MenuDrawingHelper.DrawPixelSegments(b, barStartX, barStartY, stats.SpeedIV, stats.SpeedEV, 2f);
                MenuDrawingHelper.DrawPixelSegments(b, barStartX, bar2Y, stats.SprintIV, stats.SprintEV, 2f);
                MenuDrawingHelper.DrawPixelSegments(b, barStartX, bar3Y, stats.JumpIV, stats.JumpEV, 2f);

                Utility.drawTextWithShadow(b, "Speed", Game1.smallFont, new Vector2(labelX, barStartY - 2), Game1.textColor, 1f);
                Utility.drawTextWithShadow(b, "Sprint", Game1.smallFont, new Vector2(labelX, bar2Y - 2), Game1.textColor, 1f);
                Utility.drawTextWithShadow(b, "Jump", Game1.smallFont, new Vector2(labelX, bar3Y - 2), Game1.textColor, 1f);
            }
        }

        base.draw(b);

        if (!string.IsNullOrEmpty(this.hoverText))
        {
            drawHoverText(b, this.hoverText, Game1.smallFont);
        }

        drawMouse(b);
    }
}
