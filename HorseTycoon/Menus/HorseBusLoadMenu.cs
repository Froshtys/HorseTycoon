using HorseTycoon;
using HorseTycoon.Menus;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Shown when the player clicks the bus on the Summer Horse Festival day. Lists the player's horses
/// (like <see cref="HorseSwapMenu"/>) but lets them toggle up to <c>Capacity</c> horses to load onto
/// the bus, then confirm with the depart button. Closing the menu cancels the trip entirely.
/// </summary>
public class HorseBusLoadMenu : IClickableMenu
{
    private readonly List<FarmAnimal> Animals;
    private readonly Action<List<FarmAnimal>> OnConfirm;
    private readonly int Capacity;
    private readonly HashSet<long> SelectedIds = new();

    private readonly int RowHeight = 112;
    private readonly int TopPadding = 96;
    private readonly int MaxVisibleItems = 4;
    private int startIndex = 0;
    private int HoveredIndex = -1;

    private ClickableTextureComponent upArrow;
    private ClickableTextureComponent downArrow;
    private ClickableTextureComponent departButton;

    private ClickableTextureComponent scrollBar;
    private Rectangle scrollBarRunner;
    private bool scrolling = false;

    private string hoverText = "";
    private int InputLockoutTimer = 150;

    public HorseBusLoadMenu(List<FarmAnimal> animals, IEnumerable<long> preselectedIds, int capacity, Action<List<FarmAnimal>> onConfirm)
        : base(Game1.uiViewport.Width / 2 - 375, Game1.uiViewport.Height / 2 - 290, 770, 580, showUpperRightCloseButton: true)
    {
        this.Animals = animals;
        this.OnConfirm = onConfirm;
        this.Capacity = capacity;

        foreach (long id in preselectedIds)
        {
            if (this.SelectedIds.Count >= this.Capacity) break;
            if (animals.Any(a => a.myID.Value == id && !a.isBaby()))
                this.SelectedIds.Add(id);
        }

        int rightScrollEdgeX = this.xPositionOnScreen + this.width + 16;
        this.upArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + TopPadding, 44, 48), Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
        this.downArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + this.height - 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);

        this.scrollBar = new ClickableTextureComponent(new Rectangle(this.upArrow.bounds.X + 12, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, 24, 40), Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);
        this.scrollBarRunner = new Rectangle(this.scrollBar.bounds.X, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, this.scrollBar.bounds.Width, this.downArrow.bounds.Y - this.upArrow.bounds.Y - this.upArrow.bounds.Height - 8);

        this.departButton = new ClickableTextureComponent(
            name: "Depart",
            bounds: new Rectangle(this.xPositionOnScreen + this.width - 88, this.yPositionOnScreen + this.height + 8, 64, 64),
            label: null,
            hoverText: "Board the bus",
            texture: Game1.mouseCursors,
            sourceRect: new Rectangle(128, 256, 64, 64),
            scale: 1f,
            drawShadow: true)
        {
            myID = 201,
            baseScale = 1f
        };

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

        if (this.departButton.containsPoint(x, y))
        {
            this.departButton.scale = Math.Min(this.departButton.scale + 0.02f, this.departButton.baseScale + 0.1f);
            this.hoverText = this.departButton.hoverText;
        }
        else
        {
            this.departButton.scale = Math.Max(this.departButton.scale - 0.02f, this.departButton.baseScale);
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
                    if (!animal.isBaby())
                    {
                        this.hoverText = this.SelectedIds.Contains(animal.myID.Value)
                            ? "Leave " + animal.Name + " home"
                            : "Load " + animal.Name + " onto the bus";
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

        if (this.departButton.containsPoint(x, y))
        {
            Game1.playSound("coin");
            List<FarmAnimal> selected = this.Animals.Where(a => this.SelectedIds.Contains(a.myID.Value)).ToList();
            Game1.exitActiveMenu();
            this.OnConfirm(selected);
            return;
        }

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
                    Game1.showRedMessage("Too young to travel");
                    return;
                }

                if (this.SelectedIds.Contains(animal.myID.Value))
                {
                    Game1.playSound("smallSelect");
                    this.SelectedIds.Remove(animal.myID.Value);
                }
                else if (this.SelectedIds.Count >= this.Capacity)
                {
                    Game1.playSound("cancel");
                    Game1.showRedMessage($"The bus only has room for {this.Capacity} horses");
                }
                else
                {
                    Game1.playSound("drumkit6");
                    this.SelectedIds.Add(animal.myID.Value);
                }
                break;
            }
        }
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        string titleText = "Load horses onto the bus";
        int titleWidth = SpriteText.getWidthOfString(titleText);
        int titleX = this.xPositionOnScreen + (this.width / 2) - (titleWidth / 2);
        int titleY = this.yPositionOnScreen;
        SpriteText.drawStringWithScrollBackground(b, titleText, titleX, titleY);

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), this.scrollBarRunner.X, this.scrollBarRunner.Y, this.scrollBarRunner.Width, this.scrollBarRunner.Height, Color.White, 4f, false);
        this.scrollBar.draw(b);
        this.upArrow.draw(b);
        this.downArrow.draw(b);

        Rectangle emptyBoxSource = new Rectangle(227, 425, 9, 9);
        Rectangle checkedBoxSource = new Rectangle(236, 425, 9, 9);

        for (int i = 0; i < MaxVisibleItems; i++)
        {
            int actualIndex = i + startIndex;
            if (actualIndex >= Animals.Count) break;

            var animal = Animals[actualIndex];
            bool isSelected = this.SelectedIds.Contains(animal.myID.Value);
            int relativeY = yPositionOnScreen + TopPadding + (i * RowHeight);
            int relativeX = xPositionOnScreen + 60;
            int panelX = xPositionOnScreen + 32;
            int panelWidth = width - 64;
            int panelHeight = RowHeight + 4;

            // Draw standard slot background container
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), panelX, relativeY, panelWidth, panelHeight, Color.White, 4f, false);

            if (isSelected)
            {
                b.Draw(Game1.staminaRect, new Rectangle(panelX + 4, relativeY + 4, panelWidth - 8, panelHeight - 8), Color.DarkGreen * 0.20f);
            }
            else if (i == this.HoveredIndex)
            {
                b.Draw(Game1.staminaRect, new Rectangle(panelX + 4, relativeY + 4, panelWidth - 8, panelHeight - 8), Color.SaddleBrown * 0.30f);
            }

            // Draw Animal Texture
            float scale = (i == this.HoveredIndex) ? 3.2f : 3.0f;
            b.Draw(animal.Sprite.Texture, new Vector2(relativeX + 2, relativeY + 2), animal.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            // Name and Tag Layout Engine
            string name = animal.Name;
            bool isBabyHorseRow = animal.isBaby();

            if (isSelected)
            {
                Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 16);
                Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);

                string boardingTag = "(boarding)";
                Vector2 tagSize = Game1.smallFont.MeasureString(boardingTag);
                Vector2 tagPos = new Vector2(relativeX + 110 + (240 - tagSize.X) / 2, relativeY + 58);
                Utility.drawTextWithShadow(b, boardingTag, Game1.smallFont, tagPos, Color.DarkGreen);
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

            // Boarding checkbox at the right edge of the row (babies can't board).
            if (!isBabyHorseRow)
            {
                float checkboxScale = 3.4f;
                int checkboxX = panelX + panelWidth - 52;
                int checkboxY = relativeY + (panelHeight / 2) - (int)(9 * checkboxScale / 2);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxX, checkboxY), isSelected ? checkedBoxSource : emptyBoxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
            }
        }

        // Seat count next to the depart button.
        string seatText = $"{this.SelectedIds.Count}/{this.Capacity} horses aboard";
        Vector2 seatSize = Game1.smallFont.MeasureString(seatText);
        Utility.drawTextWithShadow(b, seatText, Game1.smallFont,
            new Vector2(this.departButton.bounds.X - seatSize.X - 16, this.departButton.bounds.Y + 32 - seatSize.Y / 2),
            Color.White);

        base.draw(b);

        this.departButton.draw(b);

        if (!string.IsNullOrEmpty(this.hoverText))
        {
            drawHoverText(b, this.hoverText, Game1.smallFont);
        }

        drawMouse(b);
    }
}
