using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace HorseTycoon.Menus
{
    /// <summary>
    /// Generic horse picker: lists the given horses with their stat segments and invokes a callback
    /// with the clicked one. Used by the festival Stud Shop to choose which of the horses the player
    /// brought should be bred, and by the breeding pen's slot assignment. Babies and pregnant mares
    /// are shown but can't be selected. Closing the menu cancels (callback never fires).
    /// </summary>
    public class HorseSelectMenu : ScrollableRowMenu
    {
        private readonly string title;
        private readonly List<FarmAnimal> Animals;
        private readonly Action<FarmAnimal> OnSelected;

        protected override int ItemCount => this.Animals.Count;
        protected override string Title => this.title;

        public HorseSelectMenu(string title, List<FarmAnimal> animals, Action<FarmAnimal> onSelected)
        {
            this.title = title;
            this.Animals = animals;
            this.OnSelected = onSelected;
        }

        protected override string? GetRowHoverText(int index)
        {
            FarmAnimal animal = this.Animals[index];
            return !animal.isBaby() && !HorseHelper.IsPregnant(animal)
                ? "Choose " + animal.Name
                : null;
        }

        protected override void OnRowClicked(int index)
        {
            FarmAnimal animal = this.Animals[index];

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
        }

        protected override void DrawRow(SpriteBatch b, int index, int visibleRow, int rowY)
        {
            FarmAnimal animal = this.Animals[index];
            this.DrawHoverTint(b, visibleRow, rowY);

            int rowX = this.RowContentX;
            float scale = visibleRow == this.HoveredIndex ? 3.2f : 3.0f;
            b.Draw(animal.Sprite.Texture, new Vector2(rowX + 2, rowY + 2), animal.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            bool baby = animal.isBaby();
            bool pregnant = !baby && HorseHelper.IsPregnant(animal);
            // A hidden horse listed here is one that's currently active in a stable: choosing it pulls
            // it out and leaves that stable empty, so flag it rather than surprising the player.
            string? tag = baby ? "(baby)" : pregnant ? "(pregnant)" : HorseHelper.IsHidden(animal) ? "(in stable)" : null;
            DrawNameWithGender(b, animal.Name, animal.isMale(), tag, baby ? Color.Gray : Color.MediumVioletRed, rowX, rowY);

            var stats = animal.GetHorseStats();
            this.DrawStatSegments(b, rowX, rowY, stats.SpeedIV, stats.SpeedEV, stats.SprintIV, stats.SprintEV, stats.JumpIV, stats.JumpEV);
        }
    }
}
