using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;

public class HorseSwapMenu : IClickableMenu
{
    private readonly List<FarmAnimal> Animals;
    private readonly Action<FarmAnimal> OnSelected;

    // UI & Scrolling Settings
    private readonly int RowHeight = 100;
    private readonly int TopPadding = 110;
    private readonly int MaxVisibleItems = 4;
    private int startIndex = 0;
    private int HoveredIndex = -1;

    private ClickableTextureComponent upArrow;
    private ClickableTextureComponent downArrow;

    public HorseSwapMenu(List<FarmAnimal> animals, Action<FarmAnimal> onSelected)
        : base(Game1.uiViewport.Width / 2 - 350, Game1.uiViewport.Height / 2 - 250, 700, 500, showUpperRightCloseButton: true)
    {
        this.Animals = animals;
        this.OnSelected = onSelected;

        // Initialize Arrows (using vanilla mouseCursors texture)
        this.upArrow = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + this.width + 16, this.yPositionOnScreen + 16, 44, 48), Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
        this.downArrow = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + this.width + 16, this.yPositionOnScreen + this.height - 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        if (direction > 0 && startIndex > 0) startIndex--;
        else if (direction < 0 && startIndex < Animals.Count - MaxVisibleItems) startIndex++;
    }

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.HoveredIndex = -1;
        this.upArrow.tryHover(x, y);
        this.downArrow.tryHover(x, y);

        for (int i = 0; i < Math.Min(MaxVisibleItems, Animals.Count); i++)
        {
            Rectangle rowArea = new Rectangle(xPositionOnScreen + 25, yPositionOnScreen + TopPadding + (i * RowHeight), width - 50, RowHeight);
            if (rowArea.Contains(x, y))
            {
                this.HoveredIndex = i;
                break;
            }
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        if (upArrow.containsPoint(x, y) && startIndex > 0)
        {
            startIndex--;
            Game1.playSound("shwip");
        }
        else if (downArrow.containsPoint(x, y) && startIndex < Animals.Count - MaxVisibleItems)
        {
            startIndex++;
            Game1.playSound("shwip");
        }

        for (int i = 0; i < Math.Min(MaxVisibleItems, Animals.Count); i++)
        {
            int actualIndex = i + startIndex;
            if (actualIndex >= Animals.Count) break;

            Rectangle rowArea = new Rectangle(xPositionOnScreen + 25, yPositionOnScreen + TopPadding + (i * RowHeight), width - 50, RowHeight);
            if (rowArea.Contains(x, y))
            {
                Game1.playSound("coin");
                OnSelected(Animals[actualIndex]);
                Game1.exitActiveMenu();
                break;
            }
        }
    }

    public override void draw(SpriteBatch b)
    {
        // 1. Background Dim
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

        // 2. Main Dialogue Box
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // 3. Draw visible horses
        for (int i = 0; i < MaxVisibleItems; i++)
        {
            int actualIndex = i + startIndex;
            if (actualIndex >= Animals.Count) break;

            var animal = Animals[actualIndex];
            int relativeY = yPositionOnScreen + TopPadding + (i * RowHeight);
            int relativeX = xPositionOnScreen + 60;

            // Highlight
            if (i == this.HoveredIndex)
            {
                b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 25, relativeY - 5, width - 50, RowHeight - 5), Color.Wheat * 0.5f);
            }

            // Dividers
            b.Draw(Game1.staminaRect, new Rectangle(relativeX + 110, relativeY - 10, 2, 80), Color.SaddleBrown * 0.4f);
            b.Draw(Game1.staminaRect, new Rectangle(relativeX + 380, relativeY - 10, 2, 80), Color.SaddleBrown * 0.4f);

            // Sprite
            float scale = (i == this.HoveredIndex) ? 3.2f : 3.0f;
            b.Draw(animal.Sprite.Texture, new Vector2(relativeX + 10, relativeY), animal.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            // Name
            string name = animal.Name;
            Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
            Vector2 namePos = new Vector2(relativeX + 110 + (270 - nameSize.X) / 2, relativeY + 10);
            Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);

            // Hearts
            int friendshipLevel = animal.friendshipTowardFarmer.Value;
            int numHearts = friendshipLevel / 200;
            Vector2 heartPos = new Vector2(relativeX + 410, relativeY + 20);
            for (int j = 0; j < 5; j++)
            {
                b.Draw(Game1.mouseCursors, heartPos + new Vector2(j * 32, 0), new Rectangle(211, 428, 7, 6), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.89f);
                if (j < numHearts) b.Draw(Game1.mouseCursors, heartPos + new Vector2(j * 32, 0), new Rectangle(218, 428, 7, 6), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
            }

            // Horizontal Divider
            if (i < MaxVisibleItems - 1 && actualIndex < Animals.Count - 1)
                b.Draw(Game1.staminaRect, new Rectangle(relativeX, relativeY + RowHeight - 15, width - 120, 2), Color.SaddleBrown * 0.4f);
        }

        // 4. Draw Arrows
        if (startIndex > 0) upArrow.draw(b);
        if (startIndex < Animals.Count - MaxVisibleItems) downArrow.draw(b);

        base.draw(b);
        drawMouse(b);
    }
}