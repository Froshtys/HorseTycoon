using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using HorseTycoon.Models;

namespace HorseTycoon.Menus
{
    public class HorseStatsMenu : IClickableMenu
    {
        private readonly FarmAnimal Horse;
        private readonly ClickableTextureComponent BackButton;

        public HorseStatsMenu(FarmAnimal horse) : base(0, 0, 0, 0, true)
        {
            this.Horse = horse;
            this.width = 550;
            this.height = 450;

            // Correct way to center a menu in 1.6
            this.xPositionOnScreen = (Game1.uiViewport.Width / 2) - (this.width / 2);
            this.yPositionOnScreen = (Game1.uiViewport.Height / 2) - (this.height / 2);

            // Re-initialize the Back Button with the new coordinates
            this.BackButton = new ClickableTextureComponent(
                new Rectangle(this.xPositionOnScreen + this.width - 80, this.yPositionOnScreen + 20, 48, 48),
                Game1.mouseCursors,
                new Rectangle(352, 495, 12, 11),
                4f
            );
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.7f);

            // Draw standard menu background
            Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);
            int YPadding = 40;

            // Draw Horse Name
            string name = this.Horse.displayName;
            Utility.drawTextWithShadow(b, name, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + 64, this.yPositionOnScreen + 72 + YPadding), Game1.textColor);

            // Draw the Stats
            var stats = this.Horse.GetHorseStats();
            int startX = this.xPositionOnScreen + 80;
            int startY = this.yPositionOnScreen + 160 + YPadding;

            DrawStatBar(b, "Speed", startX, startY, stats.SpeedIV, stats.SpeedEV);
            DrawStatBar(b, "Sprint", startX, startY + 80, stats.SprintIV, stats.SprintEV);
            DrawStatBar(b, "Jump", startX, startY + 160, stats.JumpIV, stats.JumpEV);

            this.BackButton.draw(b);
            this.drawMouse(b);
        }

        private void DrawStatBar(SpriteBatch b, string label, int x, int y, int iv, int ev)
        {
            int barWidth = 400; // 4 pixels per point
            int barHeight = 24;

            Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(x, y - 35), Game1.textColor);

            // Background
            b.Draw(Game1.staminaRect, new Rectangle(x, y, barWidth, barHeight), Color.Gray);

            // IV (Dark Green)
            b.Draw(Game1.staminaRect, new Rectangle(x, y, iv * 4, barHeight), Color.Green);

            // EV (Light Green)
            b.Draw(Game1.staminaRect, new Rectangle(x + (iv * 4), y, ev * 4, barHeight), Color.LimeGreen);

            // Notches every 10 points
            for (int i = 1; i < 10; i++)
                b.Draw(Game1.staminaRect, new Rectangle(x + (i * 40), y, 2, barHeight), Color.Black * 0.4f);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.BackButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                Game1.activeClickableMenu = new AnimalQueryMenu(this.Horse);
            }
            base.receiveLeftClick(x, y, playSound);
        }
    }
}