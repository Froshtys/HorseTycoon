using HorseTycoon;
using HorseTycoon.Menus;
using HorseTycoon.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Festival shop list in the style of <see cref="HorseSwapMenu"/>: one row per <see cref="HorseOffer"/>
/// showing the horse's sprite, name, and IV stat segments — but with a gold price on the right
/// (Pierre's-shop style) instead of daily-training checkboxes. Used by both the Horse Seller
/// (buying the horse) and the Stud Shop (buying stud services). Clicking an affordable row closes
/// the menu and invokes the selection callback; the caller runs the confirm dialogue / next step.
/// </summary>
public class HorseShopMenu : IClickableMenu
{
    private readonly string Title;
    private readonly List<HorseOffer> Offers;
    private readonly Action<HorseOffer> OnSelected;

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

    // Vanilla-ShopMenu-style keeper portrait to the left of the menu, with a small
    // speech box underneath. Null when the caller doesn't provide one (or the
    // portrait sheet fails to load).
    private readonly Texture2D? Portrait;
    private readonly string? PortraitDialogue;

    /// <param name="portraitName">Character whose "Portraits/&lt;name&gt;" sheet is shown beside the
    /// menu (vanilla shop style); null = no portrait.</param>
    /// <param name="portraitDialogue">Short keeper greeting shown in a speech box under the
    /// portrait, describing what the shop is about.</param>
    public HorseShopMenu(string title, List<HorseOffer> offers, Action<HorseOffer> onSelected,
        string? portraitName = null, string? portraitDialogue = null)
        : base(Game1.uiViewport.Width / 2 - 375, Game1.uiViewport.Height / 2 - 290, 770, 580, showUpperRightCloseButton: true)
    {
        this.Title = title;
        this.Offers = offers.Where(o => !o.Purchased).ToList();
        this.OnSelected = onSelected;

        if (portraitName != null)
        {
            try
            {
                this.Portrait = Game1.content.Load<Texture2D>("Portraits/" + portraitName);
            }
            catch
            {
                Logger.LogVerbose($"HorseShopMenu: no portrait sheet found for '{portraitName}'.");
            }
        }
        if (portraitDialogue != null)
            this.PortraitDialogue = Game1.parseText(portraitDialogue, Game1.dialogueFont, 304);

        int rightScrollEdgeX = this.xPositionOnScreen + this.width + 16;
        this.upArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + TopPadding, 44, 48), Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
        this.downArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + this.height - 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);

        this.scrollBar = new ClickableTextureComponent(new Rectangle(this.upArrow.bounds.X + 12, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, 24, 40), Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);
        this.scrollBarRunner = new Rectangle(this.scrollBar.bounds.X, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, this.scrollBar.bounds.Width, this.downArrow.bounds.Y - this.upArrow.bounds.Y - this.upArrow.bounds.Height - 8);

        this.setScrollBarToCurrentIndex();
    }

    private void setScrollBarToCurrentIndex()
    {
        if (this.Offers.Count > 0)
        {
            this.scrollBar.bounds.Y = this.scrollBarRunner.Y + (this.scrollBarRunner.Height - this.scrollBar.bounds.Height) * this.startIndex / Math.Max(1, this.Offers.Count - this.MaxVisibleItems);
            if (this.startIndex == this.Offers.Count - this.MaxVisibleItems && this.Offers.Count > this.MaxVisibleItems)
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
        else if (direction < 0 && this.startIndex < this.Offers.Count - this.MaxVisibleItems)
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
            this.startIndex = Math.Max(0, Math.Min(this.Offers.Count - this.MaxVisibleItems, (int)Math.Round(percentage * (float)(this.Offers.Count - this.MaxVisibleItems))));
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

        for (int i = 0; i < Math.Min(MaxVisibleItems, Offers.Count); i++)
        {
            Rectangle rowArea = new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + TopPadding + 12 + (i * RowHeight), width - 64, RowHeight);
            if (rowArea.Contains(x, y))
            {
                this.HoveredIndex = i;
                int actualIndex = i + startIndex;
                if (actualIndex < Offers.Count)
                {
                    var offer = Offers[actualIndex];
                    this.hoverText = Game1.player.Money >= offer.Price
                        ? $"Select {offer.Name}"
                        : "Not enough gold";
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
        else if (this.downArrow.containsPoint(x, y) && this.startIndex < this.Offers.Count - this.MaxVisibleItems)
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

        for (int i = 0; i < Math.Min(MaxVisibleItems, Offers.Count); i++)
        {
            int actualIndex = i + startIndex;
            if (actualIndex >= Offers.Count) break;

            Rectangle rowArea = new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + TopPadding + 12 + (i * RowHeight), width - 64, RowHeight);
            if (rowArea.Contains(x, y))
            {
                var offer = Offers[actualIndex];

                // The row list is snapshotted when the menu opens; another player may have bought
                // this offer since (marked Purchased via the MarketOfferSold broadcast).
                if (offer.Purchased)
                {
                    Game1.playSound("cancel");
                    Game1.showRedMessage("Just sold to someone else!");
                    return;
                }

                if (Game1.player.Money < offer.Price)
                {
                    Game1.playSound("cancel");
                    Game1.showRedMessage("Not enough gold");
                    return;
                }

                Game1.playSound("coin");
                Game1.exitActiveMenu();
                this.OnSelected(offer);
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
            if (actualIndex >= Offers.Count) break;

            var offer = Offers[actualIndex];
            bool canAfford = Game1.player.Money >= offer.Price;
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

            // Draw the horse's skin texture (first sprite frame, greyed out if unaffordable)
            float scale = (i == this.HoveredIndex) ? 3.2f : 3.0f;
            Texture2D? skinTexture = HorseTexturePatches.GetTextureForSkinId(offer.SkinId);
            Color spriteTint = canAfford ? Color.White : Color.White * 0.45f;
            if (skinTexture != null)
                b.Draw(skinTexture, new Vector2(relativeX + 2, relativeY + 2), new Rectangle(0, 0, 32, 32), spriteTint, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            // Name
            Color nameColor = canAfford ? Game1.textColor : Game1.textColor * 0.5f;
            Vector2 nameSize = Game1.dialogueFont.MeasureString(offer.Name);
            Vector2 namePos = new Vector2(relativeX + 110 + (240 - nameSize.X) / 2, relativeY + 32);
            Utility.drawTextWithShadow(b, offer.Name, Game1.dialogueFont, namePos, nameColor);

            // --- IV STAT SEGMENTS (same layout as HorseSwapMenu) ---
            int barStartX = relativeX + 345;
            int barStartY = relativeY + 16;
            int verticalGap = 28;
            int bar2Y = barStartY + verticalGap;
            int bar3Y = barStartY + (verticalGap * 2);
            int labelX = barStartX + 125 + 32 + 12;

            // Dark wood partition line
            b.Draw(Game1.staminaRect, new Rectangle(relativeX + 350 - 15, relativeY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);
            MenuDrawingHelper.DrawPixelSegments(b, barStartX, barStartY, offer.SpeedIV, 0, 2f);
            MenuDrawingHelper.DrawPixelSegments(b, barStartX, bar2Y, offer.SprintIV, 0, 2f);
            MenuDrawingHelper.DrawPixelSegments(b, barStartX, bar3Y, offer.JumpIV, 0, 2f);

            Utility.drawTextWithShadow(b, "Speed", Game1.smallFont, new Vector2(labelX, barStartY - 2), Game1.textColor, 1f);
            Utility.drawTextWithShadow(b, "Sprint", Game1.smallFont, new Vector2(labelX, bar2Y - 2), Game1.textColor, 1f);
            Utility.drawTextWithShadow(b, "Jump", Game1.smallFont, new Vector2(labelX, bar3Y - 2), Game1.textColor, 1f);

            // --- GOLD PRICE (Pierre's-shop style: coin icon + amount, right-aligned) ---
            string priceText = Utility.getNumberWithCommas(offer.Price) + "g";
            Vector2 priceSize = Game1.smallFont.MeasureString(priceText);
            int coinSize = 25; // 9px coin sprite at ~2.8x
            int priceRightEdge = panelX + panelWidth - 20;
            int priceY = relativeY + panelHeight - (int)priceSize.Y - 10;
            Color priceColor = canAfford ? new Color(90, 60, 10) : Color.Firebrick;
            b.Draw(Game1.mouseCursors,
                new Vector2(priceRightEdge - priceSize.X - coinSize - 6, priceY + 2),
                new Rectangle(193, 373, 9, 10), Color.White, 0f, Vector2.Zero, 2.8f, SpriteEffects.None, 1f);
            Utility.drawTextWithShadow(b, priceText, Game1.smallFont,
                new Vector2(priceRightEdge - priceSize.X, priceY), priceColor);
        }

        // Player's current gold in the lower-left, so they can compare against prices.
        string moneyText = $"You have: {Utility.getNumberWithCommas(Game1.player.Money)}g";
        Utility.drawTextWithShadow(b, moneyText, Game1.smallFont,
            new Vector2(this.xPositionOnScreen + 40, this.yPositionOnScreen + this.height + 12), Color.White);

        base.draw(b);

        // Keeper portrait + speech box, replicating vanilla ShopMenu.draw (same offsets,
        // frame sprite, and options.showMerchantPortraits gate).
        int portraitX = this.xPositionOnScreen - 320;
        if (portraitX > 0 && Game1.options.showMerchantPortraits)
        {
            if (this.Portrait != null)
            {
                Utility.drawWithShadow(b, Game1.mouseCursors, new Vector2(portraitX, this.yPositionOnScreen),
                    new Rectangle(603, 414, 74, 74), Color.White, 0f, Vector2.Zero, 4f, flipped: false, 0.91f);
                b.Draw(this.Portrait, new Vector2(portraitX + 20, this.yPositionOnScreen + 20),
                    new Rectangle(0, 0, 64, 64), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.92f);
            }
            if (this.PortraitDialogue != null)
            {
                int dialogueX = this.xPositionOnScreen - (int)Game1.dialogueFont.MeasureString(this.PortraitDialogue).X - 64;
                if (dialogueX > 0)
                {
                    IClickableMenu.drawHoverText(b, this.PortraitDialogue, Game1.dialogueFont, 0, 0, -1, null, -1, null, null, 0, null, -1,
                        dialogueX, this.yPositionOnScreen + ((this.Portrait != null) ? 312 : 0), 1f, null, null,
                        Game1.menuTexture, new Rectangle(0, 256, 60, 60), null, null);
                }
            }
        }

        if (!string.IsNullOrEmpty(this.hoverText))
        {
            drawHoverText(b, this.hoverText, Game1.smallFont);
        }

        drawMouse(b);
    }
}
