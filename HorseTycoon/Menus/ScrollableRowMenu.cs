using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace HorseTycoon.Menus
{
    /// <summary>
    /// Shared scaffolding for the mod's scrollable horse-list menus (stable swap, bus loading,
    /// festival shops, horse picker): a 770x580 dialogue box with a title banner, up/down arrows,
    /// a draggable scrollbar, and up to <see cref="MaxVisibleItems"/> row panels with hover
    /// highlighting. Subclasses supply the item count, per-row drawing/click handling, and any
    /// extra widgets (e.g. depart / return-to-barn buttons).
    /// </summary>
    public abstract class ScrollableRowMenu : IClickableMenu
    {
        protected const int RowHeight = 112;
        protected const int TopPadding = 96;
        protected const int MaxVisibleItems = 4;
        protected const int PanelHeight = RowHeight + 4;

        /// <summary>Left edge of the stat-bar block (divider sits 10px before it), relative to
        /// <see cref="RowContentX"/>. The name column uses the space between the sprite and it.</summary>
        protected const int StatBlockX = 369;
        private const int NameColumnX = 104;
        private const int NameColumnWidth = 250;

        // Vanilla checkbox sprites shared by several subclasses.
        protected static readonly Rectangle EmptyCheckboxSource = new(227, 425, 9, 9);
        protected static readonly Rectangle CheckedCheckboxSource = new(236, 425, 9, 9);

        protected int startIndex;
        protected int HoveredIndex = -1;
        protected string hoverText = "";

        private readonly ClickableTextureComponent upArrow;
        private readonly ClickableTextureComponent downArrow;
        private readonly ClickableTextureComponent scrollBar;
        private readonly Rectangle scrollBarRunner;
        private bool scrolling;
        private int InputLockoutTimer = 150;

        /// <summary>Number of rows in the list. Must be valid once the subclass constructor has run.</summary>
        protected abstract int ItemCount { get; }

        /// <summary>Text shown in the scroll banner at the top of the menu.</summary>
        protected abstract string Title { get; }

        /// <summary>Left edge for row contents (sprite, name, stat bars).</summary>
        protected int RowContentX => this.xPositionOnScreen + 60;
        protected int PanelX => this.xPositionOnScreen + 32;
        protected int PanelWidth => this.width - 64;

        /// <summary>Extra width for menus whose rows show a gold price column
        /// (<see cref="DrawRowPrice"/>), so the price doesn't crowd the stat labels.</summary>
        protected const int PriceColumnWidth = 200;

        /// <param name="extraWidth">Widens the menu to the right (the left edge stays put so the
        /// keeper portrait and speech box keep their space). Pass <see cref="PriceColumnWidth"/>
        /// when rows draw prices.</param>
        protected ScrollableRowMenu(int extraWidth = 0)
            : base(Game1.uiViewport.Width / 2 - 375, Game1.uiViewport.Height / 2 - 290, 770 + extraWidth, 580, showUpperRightCloseButton: true)
        {
            int rightScrollEdgeX = this.xPositionOnScreen + this.width + 16;
            this.upArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + TopPadding, 44, 48), Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
            this.downArrow = new ClickableTextureComponent(new Rectangle(rightScrollEdgeX, this.yPositionOnScreen + this.height - 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
            this.scrollBar = new ClickableTextureComponent(new Rectangle(this.upArrow.bounds.X + 12, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, 24, 40), Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);
            this.scrollBarRunner = new Rectangle(this.scrollBar.bounds.X, this.upArrow.bounds.Y + this.upArrow.bounds.Height + 4, this.scrollBar.bounds.Width, this.downArrow.bounds.Y - this.upArrow.bounds.Y - this.upArrow.bounds.Height - 8);
        }

        // ---------------------------------------------------------------------
        // Subclass hooks
        // ---------------------------------------------------------------------

        /// <summary>Draw one row's contents. The panel box is already drawn; call
        /// <see cref="DrawHoverTint"/> (or <see cref="DrawRowTint"/> for a custom color) first.</summary>
        /// <param name="index">Index into the backing list.</param>
        /// <param name="visibleRow">0-based on-screen row (compare with <see cref="HoveredIndex"/>).</param>
        /// <param name="rowY">Top pixel of the row panel.</param>
        protected abstract void DrawRow(SpriteBatch b, int index, int visibleRow, int rowY);

        /// <summary>Handle a click on a row (eligibility guards + selection). Runs after
        /// scroll-widget and extra-widget click handling.</summary>
        protected abstract void OnRowClicked(int index);

        /// <summary>Hover text for a row, or null for none.</summary>
        protected virtual string? GetRowHoverText(int index) => null;

        /// <summary>Hover handling for extra widgets (buttons outside the list).</summary>
        protected virtual void HoverExtras(int x, int y) { }

        /// <summary>Click handling for extra widgets; return true when the click was consumed.</summary>
        protected virtual bool TryHandleExtraClick(int x, int y) => false;

        /// <summary>Draw extra widgets after the shared chrome (rows, close button).</summary>
        protected virtual void DrawExtras(SpriteBatch b) { }

        // ---------------------------------------------------------------------
        // Shared behavior
        // ---------------------------------------------------------------------

        protected Rectangle RowHitArea(int visibleRow) =>
            new(this.xPositionOnScreen + 24, this.yPositionOnScreen + TopPadding + 12 + visibleRow * RowHeight, this.width - 64, RowHeight);

        protected void setScrollBarToCurrentIndex()
        {
            if (this.ItemCount > 0)
            {
                this.scrollBar.bounds.Y = this.scrollBarRunner.Y + (this.scrollBarRunner.Height - this.scrollBar.bounds.Height) * this.startIndex / Math.Max(1, this.ItemCount - MaxVisibleItems);
                if (this.startIndex == this.ItemCount - MaxVisibleItems && this.ItemCount > MaxVisibleItems)
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
            else if (direction < 0 && this.startIndex < this.ItemCount - MaxVisibleItems)
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
                this.startIndex = Math.Max(0, Math.Min(this.ItemCount - MaxVisibleItems, (int)Math.Round(percentage * (float)(this.ItemCount - MaxVisibleItems))));
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
            this.HoverExtras(x, y);

            for (int i = 0; i < Math.Min(MaxVisibleItems, this.ItemCount); i++)
            {
                if (this.RowHitArea(i).Contains(x, y))
                {
                    this.HoveredIndex = i;
                    int actualIndex = i + this.startIndex;
                    if (actualIndex < this.ItemCount)
                        this.hoverText = this.GetRowHoverText(actualIndex) ?? this.hoverText;
                    break;
                }
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.InputLockoutTimer > 0) return;
            base.receiveLeftClick(x, y, playSound);

            if (this.TryHandleExtraClick(x, y))
                return;

            if (this.upArrow.containsPoint(x, y) && this.startIndex > 0)
            {
                this.startIndex--;
                Game1.playSound("shwip");
                this.setScrollBarToCurrentIndex();
                return;
            }
            if (this.downArrow.containsPoint(x, y) && this.startIndex < this.ItemCount - MaxVisibleItems)
            {
                this.startIndex++;
                Game1.playSound("shwip");
                this.setScrollBarToCurrentIndex();
                return;
            }
            if (this.scrollBar.containsPoint(x, y))
            {
                this.scrolling = true;
                return;
            }
            if (this.scrollBarRunner.Contains(x, y))
            {
                this.scrolling = true;
                this.leftClickHeld(x, y);
                return;
            }

            for (int i = 0; i < Math.Min(MaxVisibleItems, this.ItemCount); i++)
            {
                int actualIndex = i + this.startIndex;
                if (actualIndex >= this.ItemCount) break;

                if (this.RowHitArea(i).Contains(x, y))
                {
                    this.OnRowClicked(actualIndex);
                    break;
                }
            }
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
            Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);

            string title = this.Title;
            int titleX = this.xPositionOnScreen + (this.width - SpriteText.getWidthOfString(title)) / 2;
            SpriteText.drawStringWithScrollBackground(b, title, titleX, this.yPositionOnScreen);

            if (this.ItemCount > MaxVisibleItems)
            {
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), this.scrollBarRunner.X, this.scrollBarRunner.Y, this.scrollBarRunner.Width, this.scrollBarRunner.Height, Color.White, 4f, false);
                this.scrollBar.draw(b);
                this.upArrow.draw(b);
                this.downArrow.draw(b);
            }

            for (int i = 0; i < MaxVisibleItems; i++)
            {
                int actualIndex = i + this.startIndex;
                if (actualIndex >= this.ItemCount) break;

                int rowY = this.yPositionOnScreen + TopPadding + i * RowHeight;
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), this.PanelX, rowY, this.PanelWidth, PanelHeight, Color.White, 4f, false);
                this.DrawRow(b, actualIndex, i, rowY);
            }

            base.draw(b);
            this.DrawExtras(b);

            if (!string.IsNullOrEmpty(this.hoverText))
                drawHoverText(b, this.hoverText, Game1.smallFont);

            drawMouse(b);
        }

        // ---------------------------------------------------------------------
        // Shared row-drawing helpers
        // ---------------------------------------------------------------------

        /// <summary>Fill the row panel's interior with a translucent color (selection/hover states).</summary>
        protected void DrawRowTint(SpriteBatch b, int rowY, Color color) =>
            b.Draw(Game1.staminaRect, new Rectangle(this.PanelX + 4, rowY + 4, this.PanelWidth - 8, PanelHeight - 8), color);

        /// <summary>Standard brown hover tint when this visible row is under the mouse.</summary>
        protected void DrawHoverTint(SpriteBatch b, int visibleRow, int rowY)
        {
            if (visibleRow == this.HoveredIndex)
                this.DrawRowTint(b, rowY, Color.SaddleBrown * 0.30f);
        }

        /// <summary>Name centered in the row's name column, vertically centered (no tag).</summary>
        protected static void DrawCenteredName(SpriteBatch b, string name, Color color, int rowX, int rowY)
        {
            Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
            Utility.drawTextWithShadow(b, name, Game1.dialogueFont, new Vector2(rowX + NameColumnX + (NameColumnWidth - nameSize.X) / 2, rowY + 32), color);
        }

        /// <summary>Name plus an optional status tag ("(baby)", "(pregnant)", ...) underneath, both
        /// centered in the name column. A null tag falls back to the vertically-centered layout.</summary>
        protected static void DrawNameWithTag(SpriteBatch b, string name, string? tag, Color tagColor, int rowX, int rowY)
        {
            if (tag == null)
            {
                DrawCenteredName(b, name, Game1.textColor, rowX, rowY);
                return;
            }

            Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
            Utility.drawTextWithShadow(b, name, Game1.dialogueFont, new Vector2(rowX + NameColumnX + (NameColumnWidth - nameSize.X) / 2, rowY + 16), Game1.textColor);
            Vector2 tagSize = Game1.smallFont.MeasureString(tag);
            Utility.drawTextWithShadow(b, tag, Game1.smallFont, new Vector2(rowX + NameColumnX + (NameColumnWidth - tagSize.X) / 2, rowY + 58), tagColor);
        }

        /// <summary>Load a "Portraits/&lt;name&gt;" sheet for <see cref="DrawKeeperPortrait"/>; null when
        /// the name is null or the sheet is missing.</summary>
        protected static Texture2D? LoadPortrait(string? portraitName)
        {
            if (portraitName == null)
                return null;
            try
            {
                return Game1.content.Load<Texture2D>("Portraits/" + portraitName);
            }
            catch
            {
                Logger.LogVerbose($"ScrollableRowMenu: no portrait sheet found for '{portraitName}'.");
                return null;
            }
        }

        /// <summary>Keeper portrait + speech box to the left of the menu, replicating vanilla
        /// ShopMenu.draw (same offsets, frame sprite, and options.showMerchantPortraits gate).</summary>
        /// <param name="parsedDialogue">Speech-box text, already run through Game1.parseText.</param>
        protected void DrawKeeperPortrait(SpriteBatch b, Texture2D? portrait, string? parsedDialogue)
        {
            int portraitX = this.xPositionOnScreen - 320;
            if (portraitX <= 0 || !Game1.options.showMerchantPortraits)
            {
                Logger.LogVerbose($"DrawKeeperPortrait: hidden (portraitX={portraitX}, showMerchantPortraits={Game1.options.showMerchantPortraits}).");
                return;
            }

            if (portrait != null)
            {
                Utility.drawWithShadow(b, Game1.mouseCursors, new Vector2(portraitX, this.yPositionOnScreen),
                    new Rectangle(603, 414, 74, 74), Color.White, 0f, Vector2.Zero, 4f, flipped: false, 0.91f);
                b.Draw(portrait, new Vector2(portraitX + 20, this.yPositionOnScreen + 20),
                    new Rectangle(0, 0, 64, 64), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.92f);
            }
            if (parsedDialogue != null)
            {
                // Vanilla hides the speech box when it doesn't fully fit left of the menu
                // (needs ~370px vs the portrait's 320px); clamp to the screen edge instead
                // so the dialogue always shows whenever the portrait does.
                int dialogueX = Math.Max(8, this.xPositionOnScreen - (int)Game1.dialogueFont.MeasureString(parsedDialogue).X - 64);
                IClickableMenu.drawHoverText(b, parsedDialogue, Game1.dialogueFont, 0, 0, -1, null, -1, null, null, 0, null, -1,
                    dialogueX, this.yPositionOnScreen + ((portrait != null) ? 312 : 0), 1f, null, null,
                    Game1.menuTexture, new Rectangle(0, 256, 60, 60), null, null);
            }
        }

        /// <summary>Player's current gold in the lower-left, so they can compare against prices.
        /// Uses the vanilla hanging money-box sign (same widget ShopMenu draws).</summary>
        protected void DrawPlayerMoney(SpriteBatch b)
        {
            Game1.dayTimeMoneyBox.drawMoneyBox(b, this.xPositionOnScreen - 4, this.yPositionOnScreen + this.height - 12);
        }

        /// <summary>Gold amount vanilla-ShopMenu style (SpriteText digits then a shadowed coin,
        /// right-aligned, vertically centered in the row; dimmed like Pierre's when the player
        /// can't act on it). Same offsets/scales as ShopMenu.draw.</summary>
        protected void DrawRowPrice(SpriteBatch b, int rowY, int price, bool dimmed = false)
        {
            string priceText = price + " ";
            int rightEdge = this.PanelX + this.PanelWidth;
            int priceY = rowY + (PanelHeight - 44) / 2;
            SpriteText.drawString(b, priceText, rightEdge - SpriteText.getWidthOfString(priceText) - 60, priceY,
                999999, -1, 999999, dimmed ? 0.5f : 1f, 0.88f);
            Utility.drawWithShadow(b, Game1.mouseCursors, new Vector2(rightEdge - 52, priceY + 8),
                new Rectangle(193, 373, 9, 10), Color.White * (dimmed ? 0.25f : 1f), 0f, Vector2.Zero, 4f,
                flipped: false, -1f, -1, -1, dimmed ? 0f : 0.35f);
        }

        /// <summary>The three Speed/Sprint/Jump pixel-segment bars with labels and the dark wood
        /// partition line, in the standard row layout shared by every list menu.</summary>
        protected void DrawStatSegments(SpriteBatch b, int rowX, int rowY,
            int speedIV, int speedEV, int sprintIV, int sprintEV, int jumpIV, int jumpEV)
        {
            int barStartX = rowX + StatBlockX;
            int barStartY = rowY + 16;
            const int verticalGap = 28;
            int labelX = barStartX + 125 + 32 + 12;

            b.Draw(Game1.staminaRect, new Rectangle(barStartX - 10, rowY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);
            MenuDrawingHelper.DrawPixelSegments(b, barStartX, barStartY, speedIV, speedEV, 2f);
            MenuDrawingHelper.DrawPixelSegments(b, barStartX, barStartY + verticalGap, sprintIV, sprintEV, 2f);
            MenuDrawingHelper.DrawPixelSegments(b, barStartX, barStartY + verticalGap * 2, jumpIV, jumpEV, 2f);

            Utility.drawTextWithShadow(b, "Speed", Game1.smallFont, new Vector2(labelX, barStartY - 2), Game1.textColor, 1f);
            Utility.drawTextWithShadow(b, "Sprint", Game1.smallFont, new Vector2(labelX, barStartY + verticalGap - 2), Game1.textColor, 1f);
            Utility.drawTextWithShadow(b, "Jump", Game1.smallFont, new Vector2(labelX, barStartY + verticalGap * 2 - 2), Game1.textColor, 1f);
        }
    }
}
