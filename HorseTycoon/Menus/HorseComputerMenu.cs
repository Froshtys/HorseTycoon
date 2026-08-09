using System.Collections.Generic;
using HorseTycoon.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Menus;

namespace HorseTycoon.Menus
{
    /// <summary>
    /// Read-only ledger opened by the Horse Computer furniture, in two tabs: the stable records (every
    /// horse on the farm with its skills, hearts, petting and apples) and the farm's festival race
    /// results. Rows aren't clickable — this is a board to read, not a picker.
    /// </summary>
    public class HorseComputerMenu : ScrollableRowMenu
    {
        private enum Tab { Stable, Races }
        /// <summary>Extra width for the record column (hearts / petted / apples) to the right of the
        /// stat bars, matching the room the price column gets in the shop menus.</summary>
        private const int RecordColumnWidth = PriceColumnWidth;

        /// <summary>Left edge of the record column, relative to <see cref="ScrollableRowMenu.RowContentX"/>.
        /// Sits past the daily-training checkboxes, with its own partition line 10px before it
        /// (mirroring <see cref="ScrollableRowMenu.StatBlockX"/>).</summary>
        private const int RecordBlockX = 700;

        /// <summary>Left edge of the daily-training checkbox column, in the same spot relative to the
        /// stat labels that <see cref="HorseSwapMenu"/> puts it.</summary>
        private const int TrainingBlockX = StatBlockX + 272;

        /// <summary>The vanilla grab/petting hand — cursor index 2 on the 16x16 mouseCursors grid, the
        /// same hand the game shows over an animal that still wants petting. Cropped to the 10x10 the
        /// art actually fills (the rest of the tile is padding) so it sits on the row's grid rather than
        /// floating up and to the left of it.</summary>
        private static readonly Rectangle PettingHandSource = new(32, 0, 10, 10);

        /// <summary>Standing side view (row 1, first frame) of the horse skin sheet — the same frame the
        /// mannequin art was traced from (see <see cref="MannequinPatches"/>), so every row shows the
        /// horse in profile no matter what it's doing out in the barn.</summary>
        private static readonly Rectangle SideViewFrame = new(0, 32, 32, 32);

        // Vanilla heart sprites, as AnimalQueryMenu draws them: filled, empty, and the left half used
        // to show a heart that's part-way full.
        private static readonly Rectangle FullHeartSource = new(211, 428, 7, 6);
        private static readonly Rectangle EmptyHeartSource = new(218, 428, 7, 6);
        private static readonly Rectangle HalfHeartSource = new(211, 428, 4, 6);

        /// <summary>The blank vanilla tab (index 1 of the GameMenu tab strip), used as a backing plate
        /// for the mod's own icons — none of the vanilla tab icons say "horses" or "races".</summary>
        private static readonly Rectangle BlankTabSource = new(16, 368, 16, 16);

        private readonly List<FarmAnimal> Animals;
        private readonly List<RaceHistoryManager.RaceRecord> Races;
        private readonly List<TabButton> TabButtons = new();

        /// <summary>A tab's clickable area plus the icon drawn on its blank plate.</summary>
        private record TabButton(Tab Tab, Rectangle Bounds, string HoverText, Texture2D? Icon, Rectangle IconSource, float IconScale);

        private Tab currentTab = Tab.Stable;

        protected override int ItemCount => this.currentTab == Tab.Stable ? this.Animals.Count : this.Races.Count;
        protected override string Title => this.currentTab == Tab.Stable ? "Stable records" : "Race results";

        /// <param name="horseIcon">The mod's horse icon for the stable tab; null falls back to a bare tab.</param>
        public HorseComputerMenu(List<FarmAnimal> animals, Texture2D? horseIcon)
            : base(RecordColumnWidth)
        {
            this.Animals = animals;
            this.Races = RaceHistoryManager.GetHistory();

            // Vanilla GameMenu tab placement, so they tuck under the top edge of the box.
            int tabY = this.yPositionOnScreen + IClickableMenu.tabYPositionRelativeToMenuY + 64;

            this.TabButtons.Add(new TabButton(Tab.Stable,
                new Rectangle(this.xPositionOnScreen + 64, tabY, 64, 64), "Stable records",
                horseIcon, new Rectangle(0, 0, 16, 16), 2.5f));

            // The race trophy furniture doubles as the results icon; it's a Content Patcher asset, so
            // a pack that failed to load simply leaves this tab blank rather than crashing the menu.
            Texture2D? trophy = LoadTrophyIcon();
            this.TabButtons.Add(new TabButton(Tab.Races,
                new Rectangle(this.xPositionOnScreen + 128, tabY, 64, 64), "Race results",
                trophy, new Rectangle(0, 0, 32, 32), 1.25f));
        }

        private static Texture2D? LoadTrophyIcon()
        {
            try
            {
                return Game1.content.Load<Texture2D>("CP.HorseTycoon_HorseStatue");
            }
            catch
            {
                Logger.LogVerbose("HorseComputerMenu: race trophy tab icon not available.");
                return null;
            }
        }

        /// <summary>Nothing happens on a click — both lists are purely informational.</summary>
        protected override void OnRowClicked(int index) { }

        protected override string? GetRowHoverText(int index) =>
            this.currentTab == Tab.Stable ? this.Animals[index].getMoodMessage() : null;

        protected override void HoverExtras(int x, int y)
        {
            foreach (TabButton tab in this.TabButtons)
            {
                if (tab.Bounds.Contains(x, y))
                    this.hoverText = tab.HoverText;
            }
        }

        protected override bool TryHandleExtraClick(int x, int y)
        {
            foreach (TabButton tab in this.TabButtons)
            {
                if (!tab.Bounds.Contains(x, y))
                    continue;

                if (tab.Tab != this.currentTab)
                {
                    this.currentTab = tab.Tab;
                    // The lists are different lengths, so the old scroll offset means nothing here.
                    this.startIndex = 0;
                    this.setScrollBarToCurrentIndex();
                    Game1.playSound("smallSelect");
                }
                return true;
            }

            return false;
        }

        protected override void DrawExtras(SpriteBatch b)
        {
            if (this.ItemCount == 0)
            {
                string empty = this.currentTab == Tab.Stable ? "No horses on file." : "No races run yet.";
                Vector2 size = Game1.dialogueFont.MeasureString(empty);
                Utility.drawTextWithShadow(b, empty, Game1.dialogueFont,
                    new Vector2(this.xPositionOnScreen + (this.width - size.X) / 2, this.yPositionOnScreen + TopPadding + 64),
                    Game1.textColor);
            }

        }

        /// <summary>Tabs sit behind the dialogue box, vanilla-style: only their top edge shows, and the
        /// selected one drops 8px so it reads as pulled forward.</summary>
        protected override void DrawUnderChrome(SpriteBatch b)
        {
            foreach (TabButton tab in this.TabButtons)
            {
                int y = tab.Bounds.Y + (tab.Tab == this.currentTab ? 8 : 0);
                b.Draw(Game1.mouseCursors, new Vector2(tab.Bounds.X, y), BlankTabSource,
                    Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.0001f);

                if (tab.Icon == null)
                    continue;

                // Icon centered on the plate, sitting a touch high so it clears the tab's bottom lip.
                float iconWidth = tab.IconSource.Width * tab.IconScale;
                float iconHeight = tab.IconSource.Height * tab.IconScale;
                b.Draw(tab.Icon, new Vector2(tab.Bounds.X + (64 - iconWidth) / 2, y + (64 - iconHeight) / 2 - 4),
                    tab.IconSource, Color.White, 0f, Vector2.Zero, tab.IconScale, SpriteEffects.None, 0.0002f);
            }
        }

        protected override void DrawRow(SpriteBatch b, int index, int visibleRow, int rowY)
        {
            if (this.currentTab == Tab.Races)
            {
                this.DrawRaceRow(b, index, visibleRow, rowY);
                return;
            }

            FarmAnimal animal = this.Animals[index];
            this.DrawHoverTint(b, visibleRow, rowY);

            int rowX = this.RowContentX;
            float scale = visibleRow == this.HoveredIndex ? 3.2f : 3.0f;

            // Our skin/tack sheet where we have one, so the horse in the stable looks like itself —
            // always on the fixed side-view frame. The animal's live SourceRect is whatever pose it's
            // wandering in right now, which left rows showing rear views and half-turned frames.
            Texture2D? skin = HorseTexturePatches.GetTextureForAnimal(animal);
            Texture2D texture = skin ?? animal.Sprite.Texture;
            Rectangle frame = skin != null ? SideViewFrame : animal.Sprite.SourceRect;
            b.Draw(texture, new Vector2(rowX + 2, rowY + 2), frame, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            bool baby = animal.isBaby();
            bool pregnant = !baby && HorseHelper.IsPregnant(animal);
            (string? tag, Color tagColor) = baby ? ("(baby)", Color.Gray)
                : pregnant ? ("(pregnant)", Color.MediumVioletRed)
                : ((string?)null, Color.White);
            DrawNameWithGender(b, animal.Name, animal.isMale(), tag, tagColor, rowX, rowY);

            var stats = animal.GetHorseStats();
            this.DrawStatSegments(b, rowX, rowY, stats.SpeedIV, stats.SpeedEV, stats.SprintIV, stats.SprintEV, stats.JumpIV, stats.JumpEV);

            this.DrawTrainingColumn(b, animal, rowX, rowY);
            this.DrawRecordColumn(b, animal, rowX, rowY);
        }

        /// <summary>One finished festival race: which festival and year it was, who won it, and where
        /// the player reading the computer came in.</summary>
        private void DrawRaceRow(SpriteBatch b, int index, int visibleRow, int rowY)
        {
            RaceHistoryManager.RaceRecord race = this.Races[index];
            int? placement = race.PlacementFor(Game1.player.UniqueMultiplayerID);

            // A win of the player's own gets a gold wash; everything else just takes the hover tint.
            if (placement == 1)
                this.DrawRowTint(b, rowY, Color.Goldenrod * 0.28f);
            else
                this.DrawHoverTint(b, visibleRow, rowY);

            int rowX = this.RowContentX;
            const int labelY = 22;
            const int valueY = 50;

            // --- Which race ---
            DrawFittedText(b, race.FestivalName, rowX + 8, rowY + labelY - 6, StatBlockX - 20);
            Utility.drawTextWithShadow(b, $"Year {race.Year}", Game1.smallFont, new Vector2(rowX + 8, rowY + valueY + 12), Game1.textColor);

            // --- Winner ---
            int winnerX = rowX + StatBlockX;
            b.Draw(Game1.staminaRect, new Rectangle(winnerX - 10, rowY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);
            Utility.drawTextWithShadow(b, "Winner", Game1.smallFont, new Vector2(winnerX, rowY + labelY), Color.DimGray);
            DrawFittedText(b, race.WinnerName, winnerX, rowY + valueY, RecordBlockX - StatBlockX - 20);

            // --- Where the local player came in ---
            int resultX = rowX + RecordBlockX;
            b.Draw(Game1.staminaRect, new Rectangle(resultX - 10, rowY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);
            Utility.drawTextWithShadow(b, "You", Game1.smallFont, new Vector2(resultX, rowY + labelY), Color.DimGray);

            if (placement.HasValue)
            {
                string result = $"{RaceHistoryManager.Ordinal(placement.Value)} of {race.TotalRacers}";
                Color resultColor = placement.Value == 1 ? Color.DarkGoldenrod : Game1.textColor;
                Utility.drawTextWithShadow(b, result, Game1.smallFont, new Vector2(resultX, rowY + valueY + 4), resultColor);
            }
            else
                Utility.drawTextWithShadow(b, "Didn't race", Game1.smallFont, new Vector2(resultX, rowY + valueY + 4), Color.Gray);
        }

        /// <summary>Draws text in the big dialogue font, dropping to the small font when it wouldn't fit
        /// the column — festival and farm names vary far too much in length for one fixed size.</summary>
        private static void DrawFittedText(SpriteBatch b, string text, int x, int y, int maxWidth)
        {
            bool fits = Game1.dialogueFont.MeasureString(text).X <= maxWidth;
            SpriteFont font = fits ? Game1.dialogueFont : Game1.smallFont;
            Utility.drawTextWithShadow(b, text, font, new Vector2(x, y + (fits ? 0 : 8)), Game1.textColor);
        }

        /// <summary>Today's Speed/Sprint/Jump training ticks, one per stat row — the same column
        /// <see cref="HorseSwapMenu"/> draws.</summary>
        private void DrawTrainingColumn(SpriteBatch b, FarmAnimal animal, int rowX, int rowY)
        {
            const float checkboxScale = 2.6f;
            int checkboxX = rowX + TrainingBlockX;
            int lineY = rowY + 16;
            const int verticalGap = 28;

            b.Draw(Game1.mouseCursors, new Vector2(checkboxX, lineY + 2), TrainingManager.HasTrainedSpeedToday(animal) ? CheckedCheckboxSource : EmptyCheckboxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
            b.Draw(Game1.mouseCursors, new Vector2(checkboxX, lineY + verticalGap + 2), TrainingManager.HasTrainedSprintToday(animal) ? CheckedCheckboxSource : EmptyCheckboxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
            b.Draw(Game1.mouseCursors, new Vector2(checkboxX, lineY + verticalGap * 2 + 2), TrainingManager.HasTrainedJumpToday(animal) ? CheckedCheckboxSource : EmptyCheckboxSource, Color.White, 0f, Vector2.Zero, checkboxScale, SpriteEffects.None, 1f);
        }

        /// <summary>Hearts, today's petting, and the week's apples, stacked on the same three-line grid
        /// the stat bars use so the row reads across evenly.</summary>
        private void DrawRecordColumn(SpriteBatch b, FarmAnimal animal, int rowX, int rowY)
        {
            int blockX = rowX + RecordBlockX;
            int lineY = rowY + 16;
            const int verticalGap = 28;

            b.Draw(Game1.staminaRect, new Rectangle(blockX - 10, rowY + 12, 2, RowHeight - 20), Color.SaddleBrown * 0.4f);

            // --- Hearts (friendship runs 0-1000 over five hearts, as AnimalQueryMenu shows it) ---
            double loveLevel = animal.friendshipTowardFarmer.Value / 1000.0;
            int partialHeart = loveLevel * 1000.0 % 200.0 >= 100.0 ? (int)(loveLevel * 1000.0 / 200.0) : -1;
            for (int i = 0; i < 5; i++)
            {
                bool full = loveLevel * 1000.0 > (i + 1) * 195;
                b.Draw(Game1.mouseCursors, new Vector2(blockX + 26 * i, lineY), full ? FullHeartSource : EmptyHeartSource,
                    Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.89f);
                if (partialHeart == i)
                    b.Draw(Game1.mouseCursors, new Vector2(blockX + 26 * i, lineY), HalfHeartSource,
                        Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.891f);
            }

            // --- Petted today: the petting hand, then its checkbox, laid out like the apple line ---
            int petY = lineY + verticalGap;
            b.Draw(Game1.mouseCursors, new Vector2(blockX - 3, petY - 4), PettingHandSource,
                Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.89f);
            b.Draw(Game1.mouseCursors, new Vector2(blockX + 34, petY), animal.wasPet.Value ? CheckedCheckboxSource : EmptyCheckboxSource,
                Color.White, 0f, Vector2.Zero, 2.6f, SpriteEffects.None, 1f);

            // --- Apples this week ---
            int appleY = lineY + verticalGap * 2;
            ParsedItemData apple = ItemRegistry.GetDataOrErrorItem(AppleTreatManager.AppleQualifiedId);
            b.Draw(apple.GetTexture(), new Vector2(blockX - 4, appleY - 6), apple.GetSourceRect(), Color.White, 0f, Vector2.Zero, 2f, SpriteEffects.None, 0.89f);
            string appleText = $"{AppleTreatManager.TreatsThisWeek(animal)}/{AppleTreatManager.TreatsPerWeek}";
            Utility.drawTextWithShadow(b, appleText, Game1.smallFont, new Vector2(blockX + 34, appleY - 4), Game1.textColor, 1f);
        }
    }
}
