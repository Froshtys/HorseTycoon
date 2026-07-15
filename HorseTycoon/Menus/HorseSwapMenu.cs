using System;
using System.Collections.Generic;
using HorseTycoon.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Menus;

namespace HorseTycoon.Menus
{
    /// <summary>
    /// Stable management menu opened by clicking a stable: lists the barn horses so the player can
    /// pick which one occupies the stable as the active mount. Clicking the current horse (or the
    /// return-to-barn button) sends it back to the barn and leaves the stable empty.
    /// </summary>
    public class HorseSwapMenu : ScrollableRowMenu
    {
        private readonly List<FarmAnimal> Animals;
        /// <summary>Invoked with the chosen horse, or null for "return the active horse to the barn".</summary>
        private readonly Action<FarmAnimal?> OnSelected;
        private readonly Stable TargetStable;
        private readonly FarmAnimal? ActiveFarmHorse;

        private readonly ClickableTextureComponent? returnToBarnButton;

        protected override int ItemCount => this.Animals.Count;
        protected override string Title => "Choose stable horse";

        public HorseSwapMenu(List<FarmAnimal> animals, Stable stable, FarmAnimal? activeHorse, IModHelper helper, Action<FarmAnimal?> onSelected)
        {
            this.Animals = animals;
            this.OnSelected = onSelected;
            this.TargetStable = stable;
            this.ActiveFarmHorse = activeHorse;

            if (this.ActiveFarmHorse != null)
            {
                Texture2D barnIconTexture = helper.ModContent.Load<Texture2D>("assets/horse_home_icon.png");
                const int baseSize = 16;
                const float targetScale = 4f;

                this.returnToBarnButton = new ClickableTextureComponent(
                    name: "ReturnToBarn",
                    bounds: new Rectangle(this.xPositionOnScreen + this.width - 110, this.yPositionOnScreen, (int)(baseSize * targetScale), (int)(baseSize * targetScale)),
                    label: null,
                    hoverText: "Return " + this.ActiveFarmHorse.Name + " to barn",
                    texture: barnIconTexture,
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

        private bool IsActiveHorse(FarmAnimal animal) =>
            this.ActiveFarmHorse != null && animal.myID.Value == this.ActiveFarmHorse.myID.Value;

        protected override void HoverExtras(int x, int y)
        {
            if (this.returnToBarnButton == null)
                return;

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

        protected override string? GetRowHoverText(int index)
        {
            FarmAnimal animal = this.Animals[index];
            return this.IsActiveHorse(animal) ? "Return " + animal.Name + " to barn" : null;
        }

        protected override bool TryHandleExtraClick(int x, int y)
        {
            if (this.returnToBarnButton == null || !this.returnToBarnButton.containsPoint(x, y))
                return false;

            Game1.playSound("coin");
            this.OnSelected(null);
            Game1.exitActiveMenu();
            return true;
        }

        protected override void OnRowClicked(int index)
        {
            FarmAnimal animal = this.Animals[index];

            if (animal.isBaby())
            {
                Game1.playSound("cancel");
                Game1.showRedMessage("Too young to ride");
                return;
            }

            // Pregnant mares rest in the barn until the foal arrives
            if (HorseHelper.IsPregnant(animal))
            {
                Game1.playSound("cancel");
                Game1.showRedMessage("Pregnant and resting in the barn");
                return;
            }

            // Clicking the current active horse returns it to the barn; anything else swaps to it.
            Game1.playSound("coin");
            this.OnSelected(this.IsActiveHorse(animal) ? null : animal);
            Game1.exitActiveMenu();
        }

        protected override void DrawRow(SpriteBatch b, int index, int visibleRow, int rowY)
        {
            FarmAnimal animal = this.Animals[index];
            this.DrawHoverTint(b, visibleRow, rowY);

            int rowX = this.RowContentX;
            float scale = visibleRow == this.HoveredIndex ? 3.2f : 3.0f;
            bool isActiveHorseRow = this.IsActiveHorse(animal);
            Horse? worldHorseEntity = isActiveHorseRow ? this.TargetStable?.getStableHorse() : null;

            if (worldHorseEntity?.Sprite != null)
            {
                // Active stable horse: use our skin texture with the world entity's current frame
                Texture2D drawTexture = HorseTexturePatches.GetTextureForAnimal(animal) ?? worldHorseEntity.Sprite.Texture;
                b.Draw(drawTexture, new Vector2(rowX, rowY), worldHorseEntity.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);
            }
            else
            {
                b.Draw(animal.Sprite.Texture, new Vector2(rowX + 2, rowY + 2), animal.Sprite.SourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);
            }

            bool baby = animal.isBaby();
            bool pregnant = !baby && HorseHelper.IsPregnant(animal);
            (string? tag, Color tagColor) = isActiveHorseRow ? ("(current)", Color.DarkGreen)
                : baby ? ("(baby)", Color.Gray)
                : pregnant ? ("(pregnant)", Color.MediumVioletRed)
                : ((string?)null, Color.White);
            DrawNameWithTag(b, animal.Name, tag, tagColor, rowX, rowY);

            var stats = animal.GetHorseStats();
            this.DrawStatSegments(b, rowX, rowY, stats.SpeedIV, stats.SpeedEV, stats.SprintIV, stats.SprintEV, stats.JumpIV, stats.JumpEV);

            // Daily-training checkboxes to the right of the stat labels.
            const float checkboxScale = 2.6f;
            int checkboxColumnX = rowX + StatBlockX + 272;
            int barStartY = rowY + 16;
            const int verticalGap = 28;
            b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, barStartY + 2), TrainingManager.HasTrainedSpeedToday(animal) ? CheckedCheckboxSource : EmptyCheckboxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
            b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, barStartY + verticalGap + 2), TrainingManager.HasTrainedSprintToday(animal) ? CheckedCheckboxSource : EmptyCheckboxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
            b.Draw(Game1.mouseCursors, new Vector2(checkboxColumnX, barStartY + verticalGap * 2 + 2), TrainingManager.HasTrainedJumpToday(animal) ? CheckedCheckboxSource : EmptyCheckboxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
        }

        protected override void DrawExtras(SpriteBatch b)
        {
            this.returnToBarnButton?.draw(b);
        }
    }
}
