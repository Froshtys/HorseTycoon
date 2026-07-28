using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace HorseTycoon.Menus
{
    /// <summary>
    /// The Stud Shop's "offer my studs" list: one row per grown stallion the player owns, with the
    /// fee Isaac pays for its stud services (see <see cref="HorseMarket.GetStudOfferFee"/>) on the
    /// right. Clicking a row pays the player immediately and greys the row out, so multiple horses
    /// can be sold without reopening the menu; Isaac's speech box updates with a confirmation line
    /// on each sale. Already-sold horses (tracked per festival by the caller) stay greyed out.
    /// </summary>
    public class StudOfferMenu : ScrollableRowMenu
    {
        private static readonly string[] SaleLines =
        {
            "{0}, eh? A fine sire! Here's {1}g for his services.",
            "I'll put {0} straight to work. {1}g, as agreed.",
            "Now that's a pedigree! {1}g for {0}'s services.",
        };

        /// <summary>Minimum combined stat total (IVs + EVs across Speed/Sprint/Jump) Isaac accepts.</summary>
        public const int MinTotalSkill = 90;

        private readonly List<FarmAnimal> Studs;
        private readonly HashSet<long> SoldIds;
        private readonly Texture2D? Portrait;
        private string? portraitDialogue;
        private int salesMade;

        protected override int ItemCount => this.Studs.Count;
        protected override string Title => "Offer your studs";

        /// <param name="soldIds">Horses already sold this festival; clicked horses are added so the
        /// state survives closing and reopening the menu.</param>
        public StudOfferMenu(List<FarmAnimal> studs, HashSet<long> soldIds, string? portraitName)
            : base(PriceColumnWidth)
        {
            this.Studs = studs;
            this.SoldIds = soldIds;
            this.Portrait = LoadPortrait(portraitName);
            this.SetPortraitDialogue("Let's see what you've brought. I pay by pedigree. Proven blood earns proven gold.");
        }

        private void SetPortraitDialogue(string text) =>
            this.portraitDialogue = Game1.parseText(text, Game1.dialogueFont, 304);

        private static bool MeetsSkillFloor(FarmAnimal stud)
        {
            var stats = stud.GetHorseStats();
            return stats.TotalSpeed + stats.TotalSprint + stats.TotalJump >= MinTotalSkill;
        }

        protected override string GetRowHoverText(int index)
        {
            FarmAnimal stud = this.Studs[index];
            if (this.SoldIds.Contains(stud.myID.Value))
                return "Already sold";
            if (!MeetsSkillFloor(stud))
                return $"Isaac only accepts studs with at least {MinTotalSkill} total skill";
            return $"Sell {stud.Name}'s stud services for {Utility.getNumberWithCommas(HorseMarket.GetStudOfferFee(stud))}g";
        }

        protected override void OnRowClicked(int index)
        {
            FarmAnimal stud = this.Studs[index];
            if (this.SoldIds.Contains(stud.myID.Value) || !MeetsSkillFloor(stud))
            {
                Game1.playSound("cancel");
                return;
            }

            int fee = HorseMarket.GetStudOfferFee(stud);
            this.SoldIds.Add(stud.myID.Value);
            Game1.player.Money += fee;
            Game1.playSound("purchase");
            Game1.dayTimeMoneyBox.moneyShakeTimer = 800;

            string line = SaleLines[this.salesMade++ % SaleLines.Length];
            this.SetPortraitDialogue(string.Format(line, stud.Name, Utility.getNumberWithCommas(fee)));
        }

        protected override void DrawRow(SpriteBatch b, int index, int visibleRow, int rowY)
        {
            FarmAnimal stud = this.Studs[index];
            bool sold = this.SoldIds.Contains(stud.myID.Value);
            bool tooWeak = !sold && !MeetsSkillFloor(stud);
            bool sellable = !sold && !tooWeak;

            if (sellable)
                this.DrawHoverTint(b, visibleRow, rowY);
            else
                this.DrawRowTint(b, rowY, Color.Black * 0.25f);

            int rowX = this.RowContentX;
            float scale = sellable && visibleRow == this.HoveredIndex ? 3.2f : 3.0f;
            b.Draw(stud.Sprite.Texture, new Vector2(rowX + 2, rowY + 2), stud.Sprite.SourceRect,
                sellable ? Color.White : Color.White * 0.45f, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.88f);

            if (sold)
                DrawNameWithTag(b, stud.Name, "(sold)", Color.Gray, rowX, rowY);
            else if (tooWeak)
                DrawNameWithTag(b, stud.Name, "(skills too low)", Color.Gray, rowX, rowY);
            else
                DrawCenteredName(b, stud.Name, Game1.textColor, rowX, rowY);

            var stats = stud.GetHorseStats();
            this.DrawStatSegments(b, rowX, rowY, stats.SpeedIV, stats.SpeedEV, stats.SprintIV, stats.SprintEV, stats.JumpIV, stats.JumpEV);

            this.DrawRowPrice(b, rowY, HorseMarket.GetStudOfferFee(stud), dimmed: !sellable);
        }

        protected override void DrawExtras(SpriteBatch b)
        {
            this.DrawPlayerMoney(b);
            this.DrawKeeperPortrait(b, this.Portrait, this.portraitDialogue);
        }
    }
}
