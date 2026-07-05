using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace HorseTycoon
{
    /// <summary>
    /// The bus horse trailer: a farm-wide upgrade purchased from Robin (like a house upgrade — no
    /// placement menu) that appears behind the bus at the Bus Stop once built. It's required to haul
    /// horses to away festivals (e.g. the Summer Horse Festival). Robin offers it once the bus is
    /// repaired (<c>ccVault</c> mail); construction takes <see cref="BuildDays"/> days, during which a
    /// vanilla-style construction site is drawn on the trailer's footprint.
    /// </summary>
    internal static class BusTrailerManager
    {
        /// <summary>Gold cost of the trailer.</summary>
        private const int TrailerPrice = 25000;

        /// <summary>How many days Robin takes to build the trailer.</summary>
        private const int BuildDays = 2;

        /// <summary>Farm modData key holding the remaining construction days (absent = not under construction).</summary>
        private const string DaysLeftKey = "Froshty.HorseTycoon/BusTrailerDaysLeft";

        /// <summary>Farm modData key set once the trailer is finished.</summary>
        private const string BuiltKey = "Froshty.HorseTycoon/BusTrailerBuilt";

        /// <summary>Trailer sprite size in source pixels (drawn at 4x zoom like everything else).</summary>
        private const int TrailerSourceWidth = 94;
        private const int TrailerSourceHeight = 64;

        /// <summary>Pixel offset from <see cref="BusStop.busPosition"/> to the trailer's top-left corner.
        /// The bus body is 128x64 source pixels (512px wide on screen) and drives off to the left, so the
        /// trailer hitches at its right (rear) edge and follows the bus during the drive-off animation.</summary>
        private static readonly Vector2 TrailerDrawOffset = new(512f, 0f);

        /// <summary>Tile footprint of the construction site behind the bus (the bus rests at tile (21,6);
        /// its body spans 8 tiles, so the trailer starts at tile 29). 94x64 source pixels ≈ 6x4 tiles.</summary>
        private const int TrailerTileX = 29;
        private const int TrailerTileY = 6;
        private const int TrailerTilesWide = 6;
        private const int TrailerTilesHigh = 4;

        private static IModHelper helper = null!;
        private static Texture2D? trailerTexture;
        private static bool trailerTextureLoadFailed;

        /// <summary>Whether the trailer has been built and is parked behind the bus.</summary>
        public static bool IsBuilt =>
            Context.IsWorldReady && Game1.getFarm().modData.ContainsKey(BuiltKey);

        /// <summary>Whether Robin is currently building the trailer.</summary>
        public static bool IsUnderConstruction => DaysLeft > 0;

        /// <summary>Remaining construction days, or 0 when not under construction.</summary>
        private static int DaysLeft =>
            Context.IsWorldReady
                && Game1.getFarm().modData.TryGetValue(DaysLeftKey, out string value)
                && int.TryParse(value, out int days)
            ? days
            : 0;

        public static void Initialize(IModHelper modHelper)
        {
            helper = modHelper;
            helper.Events.GameLoop.DayStarted += OnDayStarted;

            var harmony = new Harmony("Froshty.HorseTycoon.BusTrailer");

            // Inject the trailer option into Robin's carpenter question menu.
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.createQuestionDialogue),
                    new[] { typeof(string), typeof(Response[]), typeof(string) }),
                prefix: new HarmonyMethod(typeof(BusTrailerManager), nameof(CreateQuestionDialogue_Prefix)));

            // Handle the injected menu option and the follow-up yes/no answer.
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.answerDialogueAction)),
                prefix: new HarmonyMethod(typeof(BusTrailerManager), nameof(AnswerDialogueAction_Prefix)));

            // Draw the finished trailer / construction site behind the bus.
            harmony.Patch(
                original: AccessTools.Method(typeof(BusStop), nameof(BusStop.draw), new[] { typeof(SpriteBatch) }),
                postfix: new HarmonyMethod(typeof(BusTrailerManager), nameof(BusStopDraw_Postfix)));
        }

        /// <summary>Host-side construction countdown, mirroring how house upgrades tick down day by day.</summary>
        private static void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (!Context.IsMainPlayer) return;

            Farm farm = Game1.getFarm();
            if (!farm.modData.TryGetValue(DaysLeftKey, out string value) || !int.TryParse(value, out int days))
                return;

            days--;
            if (days <= 0)
            {
                farm.modData.Remove(DaysLeftKey);
                farm.modData[BuiltKey] = "true";
                Game1.showGlobalMessage("Robin has finished the horse trailer at the bus stop.");
                Logger.LogVerbose("Bus horse trailer construction complete.");
            }
            else
            {
                farm.modData[DaysLeftKey] = days.ToString();
            }
        }

        // ======================== Robin's carpenter menu ========================

        /// <summary>Whether Robin should currently offer to build the trailer. Host-only (it's shared farm
        /// infrastructure, like the vanilla community upgrade) and gated on the bus being repaired.</summary>
        private static bool ShouldOfferTrailer()
        {
            return Context.IsWorldReady
                && Game1.IsMasterGame
                && Game1.MasterPlayer.mailReceived.Contains("ccVault")
                && !IsBuilt
                && !IsUnderConstruction;
        }

        /// <summary>
        /// Fires before <see cref="GameLocation.createQuestionDialogue(string, Response[], string)"/>. When
        /// Robin's carpenter menu is being built (dialog key <c>carpenter</c>), inserts the trailer option
        /// just above "Leave".
        /// </summary>
        private static void CreateQuestionDialogue_Prefix(string question, ref Response[] answerChoices, string dialogKey)
        {
            if (dialogKey != "carpenter" || !ShouldOfferTrailer())
                return;

            var choices = answerChoices.ToList();
            int leaveIndex = choices.FindIndex(r => r.responseKey == "Leave");
            if (leaveIndex < 0) leaveIndex = choices.Count;
            choices.Insert(leaveIndex, new Response("HorseTrailer", "Build a Horse Trailer"));
            answerChoices = choices.ToArray();
        }

        /// <summary>
        /// Fires before <see cref="GameLocation.answerDialogueAction"/>. Handles the injected
        /// <c>carpenter_HorseTrailer</c> menu choice (shows the offer) and the <c>busTrailer_Yes</c>
        /// confirmation (charges the player and starts construction). Returns false to skip vanilla
        /// handling for those two answers.
        /// </summary>
        private static bool AnswerDialogueAction_Prefix(GameLocation __instance, string questionAndAnswer, ref bool __result)
        {
            switch (questionAndAnswer)
            {
                case "carpenter_HorseTrailer":
                    OfferTrailer(__instance);
                    __result = true;
                    return false;

                case "busTrailer_Yes":
                    AcceptTrailer();
                    __result = true;
                    return false;

                default:
                    return true;
            }
        }

        private static void OfferTrailer(GameLocation location)
        {
            string price = Utility.getNumberWithCommas(TrailerPrice);
            location.createQuestionDialogue(
                Game1.parseText($"I could build you a horse trailer for the bus! With that hitched up, you could haul your horses to festivals out of town. It'll cost {price}g and take {BuildDays} days. Want me to get started?"),
                location.createYesNoResponses(),
                "busTrailer");
        }

        private static void AcceptTrailer()
        {
            if (Game1.player.Money < TrailerPrice)
            {
                Game1.drawObjectDialogue(Game1.content.LoadString("Strings\\UI:NotEnoughMoney3"));
                return;
            }

            Game1.player.Money -= TrailerPrice;
            Game1.getFarm().modData[DaysLeftKey] = BuildDays.ToString();

            NPC? robin = Game1.getCharacterFromName("Robin");
            if (robin != null)
            {
                robin.setNewDialogue(new Dialogue(robin, null, $"You got it! I'll head down to the bus stop and get started. Give me {BuildDays} days and you'll be hauling horses all over the valley!$h"));
                Game1.drawDialogue(robin);
            }
            Logger.LogVerbose($"Bus horse trailer purchased for {TrailerPrice}g; {BuildDays} days of construction.");
        }

        // ======================== Drawing ========================

        /// <summary>The trailer sprite, lazily loaded from <c>assets/trailer.png</c> (94x64).</summary>
        private static Texture2D? TrailerTexture
        {
            get
            {
                if (trailerTexture == null && !trailerTextureLoadFailed)
                {
                    try
                    {
                        trailerTexture = helper.ModContent.Load<Texture2D>("assets/trailer.png");
                    }
                    catch (Exception ex)
                    {
                        trailerTextureLoadFailed = true;
                        Logger.LogVerbose($"Failed to load assets/trailer.png: {ex.Message}");
                    }
                }
                return trailerTexture;
            }
        }

        /// <summary>
        /// Fires after <see cref="BusStop.draw"/>. Draws the finished trailer hitched behind the bus
        /// (anchored to <see cref="BusStop.busPosition"/> so it follows the drive-off/drive-back
        /// animations), or the construction site while Robin is still building it.
        /// </summary>
        private static void BusStopDraw_Postfix(BusStop __instance, SpriteBatch spriteBatch)
        {
            if (IsBuilt)
            {
                Texture2D? texture = TrailerTexture;
                if (texture == null) return;

                spriteBatch.Draw(
                    texture,
                    Game1.GlobalToLocal(Game1.viewport, __instance.busPosition + TrailerDrawOffset),
                    new Rectangle(0, 0, TrailerSourceWidth, TrailerSourceHeight),
                    Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None,
                    (__instance.busPosition.Y + 192f) / 10000f);
            }
            else if (IsUnderConstruction)
            {
                DrawConstructionSite(spriteBatch);
            }
        }

        /// <summary>
        /// Draws the vanilla under-construction look (dirt mounds, plus the wooden framing on the final
        /// day) over the trailer's tile footprint. Ported from <c>Building.drawInConstruction</c> with the
        /// dirt fully risen (vanilla only animates the mounds rising for a second after placement).
        /// </summary>
        private static void DrawConstructionSite(SpriteBatch b)
        {
            const int num = 16; // dirt mound height in source pixels (16 = fully risen)
            bool finalDay = DaysLeft == 1;

            int left = TrailerTileX;
            int top = TrailerTileY;
            int right = TrailerTileX + TrailerTilesWide - 1;
            int bottom = TrailerTileY + TrailerTilesHigh - 1;
            int centerX = TrailerTileX + TrailerTilesWide / 2;

            for (int k = left; k <= right; k++)
            {
                for (int l = top; l <= bottom; l++)
                {
                    Vector2 tilePos = new Vector2(k, l) * 64f;
                    int frameYOffset = 64 - num * 4 + 16;
                    Rectangle frameSource;
                    Rectangle dirtSource;
                    float dirtDepth;

                    if (k == centerX && l == bottom)
                    {
                        frameYOffset -= 4;
                        frameSource = new Rectangle(367, 277, 16, 16);
                        dirtSource = new Rectangle(367, 309, 16, num);
                        dirtDepth = (l * 64 + 64 - 1) / 10000f;
                    }
                    else if (k == left && l == top)
                    {
                        frameSource = new Rectangle(351, 261, 16, 16);
                        dirtSource = new Rectangle(351, 293, 16, num);
                        dirtDepth = (l * 64 + 64 - 1) / 10000f;
                    }
                    else if (k == right && l == top)
                    {
                        frameSource = new Rectangle(383, 261, 16, 16);
                        dirtSource = new Rectangle(383, 293, 16, num);
                        dirtDepth = (l * 64 + 64 - 1) / 10000f;
                    }
                    else if (k == right && l == bottom)
                    {
                        frameSource = new Rectangle(383, 277, 16, 16);
                        dirtSource = new Rectangle(383, 325, 16, num);
                        dirtDepth = l * 64 / 10000f;
                    }
                    else if (k == left && l == bottom)
                    {
                        frameSource = new Rectangle(351, 277, 16, 16);
                        dirtSource = new Rectangle(351, 325, 16, num);
                        dirtDepth = l * 64 / 10000f;
                    }
                    else if (k == right)
                    {
                        frameSource = new Rectangle(383, 261, 16, 16);
                        dirtSource = new Rectangle(383, 309, 16, num);
                        dirtDepth = l * 64 / 10000f;
                    }
                    else if (l == bottom)
                    {
                        frameSource = new Rectangle(367, 277, 16, 16);
                        dirtSource = new Rectangle(367, 325, 16, num);
                        dirtDepth = l * 64 / 10000f;
                    }
                    else if (k == left)
                    {
                        frameSource = new Rectangle(351, 261, 16, 16);
                        dirtSource = new Rectangle(351, 309, 16, num);
                        dirtDepth = l * 64 / 10000f;
                    }
                    else if (l == top)
                    {
                        frameSource = new Rectangle(367, 261, 16, 16);
                        dirtSource = new Rectangle(367, 293, 16, num);
                        dirtDepth = (l * 64 + 64 - 1) / 10000f;
                    }
                    else
                    {
                        // Interior tile: only the wooden framing on the final day, no dirt mound.
                        if (finalDay)
                        {
                            b.Draw(Game1.mouseCursors,
                                Game1.GlobalToLocal(Game1.viewport, tilePos) + new Vector2(0f, frameYOffset),
                                new Rectangle(367, 261, 16, 16),
                                Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1E-05f);
                        }
                        continue;
                    }

                    if (finalDay)
                    {
                        b.Draw(Game1.mouseCursors,
                            Game1.GlobalToLocal(Game1.viewport, tilePos) + new Vector2(0f, frameYOffset),
                            frameSource,
                            Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1E-05f);
                    }
                    b.Draw(Game1.mouseCursors,
                        Game1.GlobalToLocal(Game1.viewport, tilePos) + new Vector2(0f, 64 - num * 4),
                        dirtSource,
                        Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, dirtDepth);
                }
            }
        }
    }
}
