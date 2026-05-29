using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using xTile.Format;

public class HorseSwapMenu : IClickableMenu
{
    private readonly List<FarmAnimal> Animals;
    private readonly Action<FarmAnimal> OnSelected;
    private readonly Stable TargetStable;
    private readonly FarmAnimal? ActiveFarmHorse;

    // UI Layout Configuration
    private readonly int RowHeight = 100;
    private readonly int TopPadding = 110;
    private readonly int MaxVisibleItems = 4;
    private int startIndex = 0;
    private int HoveredIndex = -1;

    private ClickableTextureComponent upArrow;
    private ClickableTextureComponent downArrow;
    private ClickableTextureComponent returnToBarnButton;
    private string hoverText = "";
    private int InputLockoutTimer = 150;

    public HorseSwapMenu(List<FarmAnimal> animals, Stable stable, FarmAnimal? activeHorse, IModHelper Helper, Action<FarmAnimal> onSelected)
        : base(Game1.uiViewport.Width / 2 - 350, Game1.uiViewport.Height / 2 - 250, 700, 500, showUpperRightCloseButton: true)
    {
        this.Animals = animals;
        this.OnSelected = onSelected;
        this.TargetStable = stable;
        this.ActiveFarmHorse = activeHorse;

        // Initialize Arrows
        this.upArrow = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + this.width + 16, this.yPositionOnScreen + 16, 44, 48), Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
        this.downArrow = new ClickableTextureComponent(new Rectangle(this.xPositionOnScreen + this.width + 16, this.yPositionOnScreen + this.height - 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);

        if (this.ActiveFarmHorse != null)
        {
            Texture2D BarnIconTexture = Helper.ModContent.Load<Texture2D>("assets/horse_home_icon.png");
            int baseSize = 16;
            float targetScale = 4f; // 16px * 4f scale produces exactly 64x64 bounds

            int buttonX = this.xPositionOnScreen + this.width - 120;
            int buttonY = this.yPositionOnScreen + 16;

            this.returnToBarnButton = new ClickableTextureComponent(
                name: "ReturnToBarn",
                bounds: new Rectangle(buttonX, buttonY, (int)(baseSize * targetScale), (int)(baseSize * targetScale)),
                label: null,
                hoverText: "Return " + this.ActiveFarmHorse.Name + " to barn",
                texture: BarnIconTexture,
                sourceRect: new Rectangle(0, 0, baseSize, baseSize),
                scale: targetScale,
                drawShadow: true
            )
            {
                myID = 200,
                baseScale = targetScale
            };
        }
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (this.InputLockoutTimer > 0)
            this.InputLockoutTimer -= time.ElapsedGameTime.Milliseconds;
    }

    // Hover action for return to barn button
    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.HoveredIndex = -1;
        this.hoverText = "";

        this.upArrow.tryHover(x, y);
        this.downArrow.tryHover(x, y);

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
        if (this.InputLockoutTimer > 0) return;

        base.receiveLeftClick(x, y, playSound);

        if (this.returnToBarnButton != null && this.returnToBarnButton.containsPoint(x, y))
        {
            Game1.playSound("creak");
            OnSelected(null!); // Triggers empty callback condition block
            Game1.exitActiveMenu();
            return;
        }

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
        // Render Layer Underlay Dim
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        if (this.returnToBarnButton != null)
        {
            this.returnToBarnButton.draw(b);
        }

        if (!string.IsNullOrEmpty(this.hoverText))
        {
            drawHoverText(b, this.hoverText, Game1.smallFont);
        }

        // Render Horse List Elements
        for (int i = 0; i < MaxVisibleItems; i++)
        {
            int actualIndex = i + startIndex;
            if (actualIndex >= Animals.Count) break;

            var animal = Animals[actualIndex];
            int relativeY = yPositionOnScreen + TopPadding + (i * RowHeight);
            int relativeX = xPositionOnScreen + 60;

            if (i == this.HoveredIndex)
            {
                b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 25, relativeY - 5, width - 50, RowHeight - 5), Color.Wheat * 0.5f);
            }

            b.Draw(Game1.staminaRect, new Rectangle(relativeX + 110, relativeY - 10, 2, 80), Color.SaddleBrown * 0.4f);
            b.Draw(Game1.staminaRect, new Rectangle(relativeX + 380, relativeY - 10, 2, 80), Color.SaddleBrown * 0.4f);

            float scale = (i == this.HoveredIndex) ? 3.2f : 3.0f;
            b.Draw(animal.Sprite.Texture, new Vector2(relativeX + 10, relativeY), animal.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            string name = animal.Name;
            Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
            Vector2 namePos = new Vector2(relativeX + 110 + (270 - nameSize.X) / 2, relativeY + 10);
            Utility.drawTextWithShadow(b, name, Game1.dialogueFont, namePos, Game1.textColor);

            int friendshipLevel = animal.friendshipTowardFarmer.Value;
            int numHearts = friendshipLevel / 200;
            Vector2 heartPos = new Vector2(relativeX + 410, relativeY + 20);
            for (int j = 0; j < 5; j++)
            {
                b.Draw(Game1.mouseCursors, heartPos + new Vector2(j * 32, 0), new Rectangle(211, 428, 7, 6), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.89f);
                if (j < numHearts) b.Draw(Game1.mouseCursors, heartPos + new Vector2(j * 32, 0), new Rectangle(218, 428, 7, 6), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
            }

            if (i < MaxVisibleItems - 1 && actualIndex < Animals.Count - 1)
                b.Draw(Game1.staminaRect, new Rectangle(relativeX, relativeY + RowHeight - 15, width - 120, 2), Color.SaddleBrown * 0.4f);
        }

        if (startIndex > 0) upArrow.draw(b);
        if (startIndex < Animals.Count - MaxVisibleItems) downArrow.draw(b);

        base.draw(b);
        drawMouse(b);
    }
}