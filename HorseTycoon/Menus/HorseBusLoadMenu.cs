using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace HorseTycoon.Menus
{
    /// <summary>
    /// Shown when the player clicks the bus on the Summer Horse Festival day. Lists the player's
    /// horses but lets them toggle up to <c>Capacity</c> horses to load onto the bus, then confirm
    /// with the depart button. Closing the menu cancels the trip entirely.
    /// </summary>
    public class HorseBusLoadMenu : ScrollableRowMenu
    {
        private readonly List<FarmAnimal> Animals;
        private readonly Action<List<FarmAnimal>> OnConfirm;
        private readonly int Capacity;
        private readonly HashSet<long> SelectedIds = new();

        private readonly ClickableTextureComponent departButton;

        protected override int ItemCount => this.Animals.Count;
        protected override string Title => "Load the horse trailer";

        public HorseBusLoadMenu(List<FarmAnimal> animals, IEnumerable<long> preselectedIds, int capacity, Action<List<FarmAnimal>> onConfirm)
        {
            this.Animals = animals;
            this.OnConfirm = onConfirm;
            this.Capacity = capacity;

            foreach (long id in preselectedIds)
            {
                if (this.SelectedIds.Count >= this.Capacity) break;
                if (animals.Any(a => a.myID.Value == id && !a.isBaby() && !HorseHelper.IsPregnant(a)))
                    this.SelectedIds.Add(id);
            }

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
        }

        protected override void HoverExtras(int x, int y)
        {
            if (this.departButton.containsPoint(x, y))
            {
                this.departButton.scale = Math.Min(this.departButton.scale + 0.02f, this.departButton.baseScale + 0.1f);
                this.hoverText = this.departButton.hoverText;
            }
            else
            {
                this.departButton.scale = Math.Max(this.departButton.scale - 0.02f, this.departButton.baseScale);
            }
        }

        protected override string? GetRowHoverText(int index)
        {
            FarmAnimal animal = this.Animals[index];
            if (animal.isBaby() || HorseHelper.IsPregnant(animal))
                return null;
            return this.SelectedIds.Contains(animal.myID.Value)
                ? "Leave " + animal.Name + " home"
                : "Load " + animal.Name + " onto the bus";
        }

        protected override bool TryHandleExtraClick(int x, int y)
        {
            if (!this.departButton.containsPoint(x, y))
                return false;

            Game1.playSound("coin");
            List<FarmAnimal> selected = this.Animals.Where(a => this.SelectedIds.Contains(a.myID.Value)).ToList();
            Game1.exitActiveMenu();
            this.OnConfirm(selected);
            return true;
        }

        protected override void OnRowClicked(int index)
        {
            FarmAnimal animal = this.Animals[index];

            if (animal.isBaby())
            {
                Game1.playSound("cancel");
                Game1.showRedMessage("Too young to travel");
                return;
            }

            // Pregnant mares stay home to rest
            if (HorseHelper.IsPregnant(animal))
            {
                Game1.playSound("cancel");
                Game1.showRedMessage("Pregnant and resting in the barn");
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
        }

        protected override void DrawRow(SpriteBatch b, int index, int visibleRow, int rowY)
        {
            FarmAnimal animal = this.Animals[index];
            bool isSelected = this.SelectedIds.Contains(animal.myID.Value);

            if (isSelected)
                this.DrawRowTint(b, rowY, Color.DarkGreen * 0.20f);
            else
                this.DrawHoverTint(b, visibleRow, rowY);

            int rowX = this.RowContentX;
            float scale = visibleRow == this.HoveredIndex ? 3.2f : 3.0f;
            b.Draw(animal.Sprite.Texture, new Vector2(rowX + 2, rowY + 2), animal.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            bool baby = animal.isBaby();
            bool pregnant = !baby && HorseHelper.IsPregnant(animal);
            (string? tag, Color tagColor) = isSelected ? ("(boarding)", Color.DarkGreen)
                : baby ? ("(baby)", Color.Gray)
                : pregnant ? ("(pregnant)", Color.MediumVioletRed)
                : ((string?)null, Color.White);
            DrawNameWithGender(b, animal.Name, animal.isMale(), tag, tagColor, rowX, rowY);

            var stats = animal.GetHorseStats();
            this.DrawStatSegments(b, rowX, rowY, stats.SpeedIV, stats.SpeedEV, stats.SprintIV, stats.SprintEV, stats.JumpIV, stats.JumpEV);

            // Boarding checkbox at the right edge of the row (babies and pregnant mares can't board).
            if (!baby && !pregnant)
            {
                float checkboxScale = 3.4f;
                int checkboxX = this.PanelX + this.PanelWidth - 52;
                int checkboxY = rowY + (PanelHeight / 2) - (int)(9 * checkboxScale / 2);
                b.Draw(Game1.mouseCursors, new Vector2(checkboxX, checkboxY), isSelected ? CheckedCheckboxSource : EmptyCheckboxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
            }
        }

        protected override void DrawExtras(SpriteBatch b)
        {
            // Seat count next to the depart button.
            string seatText = $"{this.SelectedIds.Count}/{this.Capacity} horses aboard";
            Vector2 seatSize = Game1.smallFont.MeasureString(seatText);
            Utility.drawTextWithShadow(b, seatText, Game1.smallFont,
                new Vector2(this.departButton.bounds.X - seatSize.X - 16, this.departButton.bounds.Y + 32 - seatSize.Y / 2),
                Color.White);

            this.departButton.draw(b);
        }
    }
}
