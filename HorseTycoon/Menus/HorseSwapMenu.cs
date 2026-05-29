using HorseTycoon;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using System;
using System.Collections.Generic;

// Added partial keyword to cleanly separate file chunks
public partial class HorseSwapMenu : IClickableMenu
{
    private readonly List<FarmAnimal> Animals;
    private readonly Action<FarmAnimal> OnSelected;
    private readonly Stable TargetStable;
    private readonly FarmAnimal? ActiveFarmHorse;

    private readonly int RowHeight = 112;
    private readonly int TopPadding = 96;
    private readonly int MaxVisibleItems = 4;
    private int startIndex = 0;
    private int HoveredIndex = -1;

    private ClickableTextureComponent upArrow;
    private ClickableTextureComponent downArrow;
    private ClickableTextureComponent returnToBarnButton;

    private ClickableTextureComponent scrollBar;
    private Rectangle scrollBarRunner;
    private bool scrolling = false;

    private string hoverText = "";
    private int InputLockoutTimer = 150;

    public HorseSwapMenu(List<FarmAnimal> animals, Stable stable, FarmAnimal? activeHorse, IModHelper Helper, Action<FarmAnimal> onSelected)
        : base(Game1.uiViewport.Width / 2 - 375, Game1.uiViewport.Height / 2 - 290, 766, 580, showUpperRightCloseButton: true)
    {
        this.Animals = animals;
        this.OnSelected = onSelected;
        this.TargetStable = stable;
        this.ActiveFarmHorse = activeHorse;

        int rightScrollEdgeX = this.xPositionOnScreen + this.width + 16;
        this.upArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + TopPadding, 44, 48), Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
        this.downArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + this.height - 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);

        this.scrollBar = new ClickableTextureComponent(new Rectangle(this.upArrow.bounds.X + 12, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, 24, 40), Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);
        this.scrollBarRunner = new Rectangle(this.scrollBar.bounds.X, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, this.scrollBar.bounds.Width, this.downArrow.bounds.Y - this.upArrow.bounds.Y - this.upArrow.bounds.Height - 8);

        if (this.ActiveFarmHorse != null)
        {
            Texture2D BarnIconTexture = Helper.ModContent.Load<Texture2D>("assets/horse_home_icon.png");
            int baseSize = 16;
            float targetScale = 4f;

            int buttonX = this.xPositionOnScreen + this.width - 120;
            int buttonY = this.yPositionOnScreen + 24;

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
}