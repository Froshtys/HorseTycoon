using System;
using HorseTycoon;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Buildings;
using StardewValley.Menus;

namespace HorseTycoon.Menus
{
    /// <summary>
    /// Two-slot management menu for a horse breeding pen: a Mare slot and a Stallion slot.
    /// Clicking an empty slot opens a <see cref="HorseSelectMenu"/> filtered by sex; clicking a
    /// filled slot removes that horse (returning it to its barn). Shows fed status and, once both
    /// are fed, the days remaining until the mare becomes pregnant.
    /// </summary>
    public class BreedingPenMenu : IClickableMenu
    {
        private readonly Building Pen;
        private Rectangle MareSlot;
        private Rectangle StallionSlot;
        private string hoverText = "";

        private const int SlotWidth = 300;
        private const int SlotHeight = 320;

        public BreedingPenMenu(Building pen)
            : base(Game1.uiViewport.Width / 2 - 360, Game1.uiViewport.Height / 2 - 240, 720, 480, showUpperRightCloseButton: true)
        {
            this.Pen = pen;
            int slotY = this.yPositionOnScreen + 96;
            this.MareSlot = new Rectangle(this.xPositionOnScreen + 40, slotY, SlotWidth, SlotHeight);
            this.StallionSlot = new Rectangle(this.xPositionOnScreen + this.width - SlotWidth - 40, slotY, SlotWidth, SlotHeight);
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);
            this.hoverText = "";
            if (this.MareSlot.Contains(x, y))
                this.hoverText = BreedingPenManager.GetMare(this.Pen) != null ? "Remove mare" : "Add a mare";
            else if (this.StallionSlot.Contains(x, y))
                this.hoverText = BreedingPenManager.GetStallion(this.Pen) != null ? "Remove stallion" : "Add a stallion";
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (this.MareSlot.Contains(x, y))
            {
                HandleSlotClick(asMare: true);
                return;
            }
            if (this.StallionSlot.Contains(x, y))
            {
                HandleSlotClick(asMare: false);
                return;
            }
        }

        private void HandleSlotClick(bool asMare)
        {
            FarmAnimal? current = asMare ? BreedingPenManager.GetMare(this.Pen) : BreedingPenManager.GetStallion(this.Pen);

            if (current != null)
            {
                // Occupied slot → remove and return the horse to its barn.
                Game1.playSound("coin");
                BreedingPenManager.RemoveHorse(this.Pen, wasMare: asMare);
                return;
            }

            // Empty slot → pick from eligible horses of the matching sex.
            var eligible = BreedingPenManager.GetEligible(wantMale: !asMare);
            if (eligible.Count == 0)
            {
                Game1.playSound("cancel");
                Game1.showRedMessage(asMare ? "No eligible mares available." : "No eligible stallions available.");
                return;
            }

            Building pen = this.Pen;
            Game1.playSound("shwip");
            Game1.activeClickableMenu = new HorseSelectMenu(
                asMare ? "Choose a Mare" : "Choose a Stallion",
                eligible,
                selected =>
                {
                    BreedingPenManager.AssignHorse(pen, selected, asMare: asMare);
                    Game1.activeClickableMenu = new BreedingPenMenu(pen);
                });
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
            Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);

            string title = "Breeding Pen";
            int titleWidth = SpriteText.getWidthOfString(title);
            SpriteText.drawStringWithScrollBackground(b, title, this.xPositionOnScreen + this.width / 2 - titleWidth / 2, this.yPositionOnScreen);

            DrawSlot(b, this.MareSlot, "Mare", BreedingPenManager.GetMare(this.Pen), BreedingPenManager.IsHorseFed(this.Pen, mare: true));
            DrawSlot(b, this.StallionSlot, "Stallion", BreedingPenManager.GetStallion(this.Pen), BreedingPenManager.IsHorseFed(this.Pen, mare: false));

            // Status line along the bottom.
            int days = BreedingPenManager.GetBreedDaysLeft(this.Pen);
            string status = days > 0
                ? $"Breeding in progress. {days} day{(days == 1 ? "" : "s")} until pregnancy."
                : "Add a mare and a stallion, then feed each a Gold Carrot.";
            Vector2 statusSize = Game1.smallFont.MeasureString(status);
            Utility.drawTextWithShadow(b, status, Game1.smallFont,
                new Vector2(this.xPositionOnScreen + this.width / 2 - statusSize.X / 2, this.yPositionOnScreen + this.height - 64),
                days > 0 ? Color.MediumVioletRed : Game1.textColor);

            base.draw(b);

            if (!string.IsNullOrEmpty(this.hoverText))
                drawHoverText(b, this.hoverText, Game1.smallFont);

            drawMouse(b);
        }

        private static void DrawSlot(SpriteBatch b, Rectangle slot, string label, FarmAnimal? animal, bool fed)
        {
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), slot.X, slot.Y, slot.Width, slot.Height, Color.White, 4f, false);

            int labelWidth = SpriteText.getWidthOfString(label);
            SpriteText.drawString(b, label, slot.X + slot.Width / 2 - labelWidth / 2, slot.Y + 12);

            if (animal == null)
            {
                string empty = "(empty)";
                Vector2 size = Game1.dialogueFont.MeasureString(empty);
                Utility.drawTextWithShadow(b, empty, Game1.dialogueFont,
                    new Vector2(slot.X + slot.Width / 2 - size.X / 2, slot.Y + slot.Height / 2 - size.Y / 2), Color.Gray);
                return;
            }

            // Horse portrait.
            float scale = 4f;
            Rectangle src = animal.Sprite.SourceRect;
            b.Draw(animal.Sprite.Texture,
                new Vector2(slot.X + slot.Width / 2 - src.Width * scale / 2f, slot.Y + 80),
                src, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            // Name.
            Vector2 nameSize = Game1.dialogueFont.MeasureString(animal.displayName);
            Utility.drawTextWithShadow(b, animal.displayName, Game1.dialogueFont,
                new Vector2(slot.X + slot.Width / 2 - nameSize.X / 2, slot.Y + slot.Height - 96), Game1.textColor);

            // Fed status.
            string fedText = fed ? "Fed ✓" : "Not fed";
            Color fedColor = fed ? Color.ForestGreen : Color.Firebrick;
            Vector2 fedSize = Game1.smallFont.MeasureString(fedText);
            Utility.drawTextWithShadow(b, fedText, Game1.smallFont,
                new Vector2(slot.X + slot.Width / 2 - fedSize.X / 2, slot.Y + slot.Height - 48), fedColor);
        }
    }
}
