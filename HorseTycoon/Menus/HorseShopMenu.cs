using System;
using System.Collections.Generic;
using System.Linq;
using HorseTycoon.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace HorseTycoon.Menus
{
    /// <summary>
    /// Festival shop list: one row per <see cref="HorseOffer"/> showing the horse's sprite, name,
    /// and IV stat segments — with a gold price on the right (Pierre's-shop style). Used by both
    /// the Horse Seller (buying the horse) and the Stud Shop (buying stud services). Clicking an
    /// affordable row closes the menu and invokes the selection callback; the caller runs the
    /// confirm dialogue / next step.
    /// </summary>
    public class HorseShopMenu : ScrollableRowMenu
    {
        private readonly string title;
        private readonly List<HorseOffer> Offers;
        private readonly Action<HorseOffer> OnSelected;

        // Vanilla-ShopMenu-style keeper portrait to the left of the menu, with a small
        // speech box underneath. Null when the caller doesn't provide one (or the
        // portrait sheet fails to load).
        private readonly Texture2D? Portrait;
        private readonly string? PortraitDialogue;

        protected override int ItemCount => this.Offers.Count;
        protected override string Title => this.title;

        /// <param name="portraitName">Character whose "Portraits/&lt;name&gt;" sheet is shown beside the
        /// menu (vanilla shop style); null = no portrait.</param>
        /// <param name="portraitDialogue">Short keeper greeting shown in a speech box under the
        /// portrait, describing what the shop is about.</param>
        public HorseShopMenu(string title, List<HorseOffer> offers, Action<HorseOffer> onSelected,
            string? portraitName = null, string? portraitDialogue = null)
        {
            this.title = title;
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
        }

        protected override string GetRowHoverText(int index)
        {
            HorseOffer offer = this.Offers[index];
            return Game1.player.Money >= offer.Price
                ? $"Select {offer.Name}"
                : "Not enough gold";
        }

        protected override void OnRowClicked(int index)
        {
            HorseOffer offer = this.Offers[index];

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
        }

        protected override void DrawRow(SpriteBatch b, int index, int visibleRow, int rowY)
        {
            HorseOffer offer = this.Offers[index];
            bool canAfford = Game1.player.Money >= offer.Price;
            this.DrawHoverTint(b, visibleRow, rowY);

            // Horse's skin texture (first sprite frame, greyed out if unaffordable).
            int rowX = this.RowContentX;
            float scale = visibleRow == this.HoveredIndex ? 3.2f : 3.0f;
            Texture2D? skinTexture = HorseTexturePatches.GetTextureForSkinId(offer.SkinId);
            if (skinTexture != null)
                b.Draw(skinTexture, new Vector2(rowX + 2, rowY + 2), new Rectangle(0, 0, 32, 32), canAfford ? Color.White : Color.White * 0.45f, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            DrawCenteredName(b, offer.Name, canAfford ? Game1.textColor : Game1.textColor * 0.5f, rowX, rowY);

            this.DrawStatSegments(b, rowX, rowY, offer.SpeedIV, 0, offer.SprintIV, 0, offer.JumpIV, 0);

            // Gold price (Pierre's-shop style: coin icon + amount, right-aligned).
            string priceText = Utility.getNumberWithCommas(offer.Price) + "g";
            Vector2 priceSize = Game1.smallFont.MeasureString(priceText);
            int coinSize = 25; // 9px coin sprite at ~2.8x
            int priceRightEdge = this.PanelX + this.PanelWidth - 20;
            int priceY = rowY + PanelHeight - (int)priceSize.Y - 10;
            Color priceColor = canAfford ? new Color(90, 60, 10) : Color.Firebrick;
            b.Draw(Game1.mouseCursors,
                new Vector2(priceRightEdge - priceSize.X - coinSize - 6, priceY + 2),
                new Rectangle(193, 373, 9, 10), Color.White, 0f, Vector2.Zero, 2.8f, SpriteEffects.None, 1f);
            Utility.drawTextWithShadow(b, priceText, Game1.smallFont,
                new Vector2(priceRightEdge - priceSize.X, priceY), priceColor);
        }

        protected override void DrawExtras(SpriteBatch b)
        {
            // Player's current gold in the lower-left, so they can compare against prices.
            string moneyText = $"You have: {Utility.getNumberWithCommas(Game1.player.Money)}g";
            Utility.drawTextWithShadow(b, moneyText, Game1.smallFont,
                new Vector2(this.xPositionOnScreen + 40, this.yPositionOnScreen + this.height + 12), Color.White);

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
        }
    }
}
