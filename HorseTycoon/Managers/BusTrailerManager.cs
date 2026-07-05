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
    /// repaired (<c>ccVault</c> mail), then offers two further upgrades (Big and Deluxe trailers)
    /// that raise the horse capacity. Each build takes <see cref="BuildDays"/> days, during which a
    /// vanilla-style construction site is drawn on the trailer's footprint.
    /// </summary>
    internal static class BusTrailerManager
    {
        /// <summary>One trailer upgrade tier Robin can build. Sprites are all 64px tall but grow longer
        /// each tier (94 / 126 / 158 source pixels), so each tier also has a wider construction footprint.</summary>
        private record TrailerTier(string Name, int Price, string BarItemId, string BarName, int BarCount, int HorseCapacity, string TextureName, int TilesWide);

        /// <summary>The upgrade ladder; index 0 is trailer level 1. Each tier needs gold plus smelted bars.</summary>
        private static readonly TrailerTier[] Tiers =
        {
            new("Horse Trailer", 10000, "(O)335", "Iron Bar", 5, 2, "assets/trailer.png", 6),
            new("Big Trailer", 25000, "(O)336", "Gold Bar", 5, 4, "assets/trailer_big.png", 8),
            new("Deluxe Trailer", 50000, "(O)337", "Iridium Bar", 5, 8, "assets/trailer_deluxe.png", 10),
        };

        /// <summary>How many days Robin takes to build each trailer tier.</summary>
        private const int BuildDays = 2;

        /// <summary>Farm modData key holding the remaining construction days (absent = not under construction).</summary>
        private const string DaysLeftKey = "Froshty.HorseTycoon/BusTrailerDaysLeft";

        /// <summary>Farm modData key holding the trailer level under construction (1-3).</summary>
        private const string TargetLevelKey = "Froshty.HorseTycoon/BusTrailerTargetLevel";

        /// <summary>Farm modData key holding the current trailer level (1-3, absent = no trailer).</summary>
        private const string LevelKey = "Froshty.HorseTycoon/BusTrailerLevel";

        /// <summary>Legacy farm modData key from before tiered trailers; treated as level 1.</summary>
        private const string BuiltKey = "Froshty.HorseTycoon/BusTrailerBuilt";

        /// <summary>Pixel offset from <see cref="BusStop.busPosition"/> to the trailer's top-left corner.
        /// The bus body is 128x64 source pixels (512px wide on screen) and drives off to the left, so the
        /// trailer hitches at its right (rear) edge and follows the bus during the drive-off animation.</summary>
        private static readonly Vector2 TrailerDrawOffset = new(512f, 0f);

        /// <summary>Tile footprint anchor of the construction site behind the bus (the bus rests at tile
        /// (21,6); its body spans 8 tiles, so the trailer starts at tile 29). Width varies per tier.</summary>
        private const int TrailerTileX = 29;
        private const int TrailerTileY = 6;
        private const int TrailerTilesHigh = 4;

        private static IModHelper helper = null!;
        private static readonly Dictionary<int, Texture2D?> trailerTextures = new();

        /// <summary>Current trailer level: 0 = none, 1 = Horse Trailer, 2 = Big Trailer, 3 = Deluxe Trailer.
        /// Saves from before tiered trailers (legacy <see cref="BuiltKey"/>) count as level 1.</summary>
        public static int Level
        {
            get
            {
                if (!Context.IsWorldReady) return 0;
                var modData = Game1.getFarm().modData;
                if (modData.TryGetValue(LevelKey, out string value) && int.TryParse(value, out int level))
                    return level;
                return modData.ContainsKey(BuiltKey) ? 1 : 0;
            }
        }

        /// <summary>Whether a trailer (any tier) has been built and is parked behind the bus.</summary>
        public static bool IsBuilt => Level >= 1;

        /// <summary>How many horses fit in the current trailer (0 without one).</summary>
        public static int HorseCapacity => Level >= 1 ? Tiers[System.Math.Min(Level, Tiers.Length) - 1].HorseCapacity : 0;

        /// <summary>Whether Robin is currently building the trailer or an upgrade to it.</summary>
        public static bool IsUnderConstruction => DaysLeft > 0;

        /// <summary>Remaining construction days, or 0 when not under construction.</summary>
        private static int DaysLeft =>
            Context.IsWorldReady
                && Game1.getFarm().modData.TryGetValue(DaysLeftKey, out string value)
                && int.TryParse(value, out int days)
            ? days
            : 0;

        /// <summary>The trailer level Robin is currently building, or the current level when idle.
        /// Old saves mid-construction without a target recorded were building level 1.</summary>
        private static int TargetLevel =>
            Context.IsWorldReady
                && Game1.getFarm().modData.TryGetValue(TargetLevelKey, out string value)
                && int.TryParse(value, out int level)
            ? level
            : System.Math.Max(Level, 1);

        /// <summary>The next tier Robin can offer, or null when the trailer is maxed out.</summary>
        private static TrailerTier? NextTier => Level < Tiers.Length ? Tiers[Level] : null;

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
                int level = TargetLevel;
                farm.modData.Remove(DaysLeftKey);
                farm.modData.Remove(TargetLevelKey);
                farm.modData[LevelKey] = level.ToString();
                string name = Tiers[System.Math.Min(level, Tiers.Length) - 1].Name.ToLower();
                Game1.showGlobalMessage($"Robin has finished the {name} at the bus stop.");
                Logger.LogVerbose($"Bus horse trailer construction complete (level {level}).");
            }
            else
            {
                farm.modData[DaysLeftKey] = days.ToString();
            }
        }

        // ======================== Robin's carpenter menu ========================

        /// <summary>Whether Robin should currently offer the next trailer tier. Host-only (it's shared farm
        /// infrastructure, like the vanilla community upgrade) and gated on the bus being repaired.</summary>
        private static bool ShouldOfferTrailer()
        {
            return Context.IsWorldReady
                && Game1.IsMasterGame
                && Game1.MasterPlayer.mailReceived.Contains("ccVault")
                && NextTier != null
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

            string label = Level == 0
                ? $"Build a {NextTier!.Name}"
                : $"Upgrade to a {NextTier!.Name}";

            var choices = answerChoices.ToList();
            int leaveIndex = choices.FindIndex(r => r.responseKey == "Leave");
            if (leaveIndex < 0) leaveIndex = choices.Count;
            choices.Insert(leaveIndex, new Response("HorseTrailer", label));
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
            TrailerTier? tier = NextTier;
            if (tier == null) return;

            string price = Utility.getNumberWithCommas(tier.Price);
            string pitch = Level == 0
                ? $"I could build you a horse trailer for the bus! With that hitched up, you could haul your horses to festivals out of town. It'll cost {price}g plus {tier.BarCount} {tier.BarName}s and take {BuildDays} days. Want me to get started?"
                : $"I could stretch that trailer into a {tier.Name.ToLower()} — room for {tier.HorseCapacity} horses! It'll cost {price}g plus {tier.BarCount} {tier.BarName}s and take {BuildDays} days. Want me to get started?";

            location.createQuestionDialogue(
                Game1.parseText(pitch),
                location.createYesNoResponses(),
                "busTrailer");
        }

        private static void AcceptTrailer()
        {
            TrailerTier? tier = NextTier;
            if (tier == null) return;

            if (Game1.player.Money < tier.Price)
            {
                Game1.drawObjectDialogue(Game1.content.LoadString("Strings\\UI:NotEnoughMoney3"));
                return;
            }
            if (Game1.player.Items.CountId(tier.BarItemId) < tier.BarCount)
            {
                Game1.drawObjectDialogue($"You'll need to bring me {tier.BarCount} {tier.BarName}s for the {tier.Name.ToLower()}.");
                return;
            }

            Game1.player.Money -= tier.Price;
            Game1.player.Items.ReduceId(tier.BarItemId, tier.BarCount);
            Farm farm = Game1.getFarm();
            farm.modData[DaysLeftKey] = BuildDays.ToString();
            farm.modData[TargetLevelKey] = (Level + 1).ToString();

            NPC? robin = Game1.getCharacterFromName("Robin");
            if (robin != null)
            {
                robin.setNewDialogue(new Dialogue(robin, null, $"You got it! I'll head down to the bus stop and get started. Give me {BuildDays} days and you'll be hauling horses all over the valley!$h"));
                Game1.drawDialogue(robin);
            }
            Logger.LogVerbose($"{tier.Name} purchased for {tier.Price}g + {tier.BarCount} {tier.BarName}s; {BuildDays} days of construction.");
        }

        // ======================== Drawing ========================

        /// <summary>The trailer sprite for the given level, lazily loaded from the tier's asset (all
        /// 64px tall, longer each tier: 94/126/158). A missing tier sprite falls back to the base
        /// trailer art so upgrades keep working before the final art exists.</summary>
        private static Texture2D? TrailerTextureFor(int level)
        {
            if (level < 1) return null;
            level = System.Math.Min(level, Tiers.Length);

            if (!trailerTextures.TryGetValue(level, out Texture2D? texture))
            {
                try
                {
                    texture = helper.ModContent.Load<Texture2D>(Tiers[level - 1].TextureName);
                }
                catch (Exception ex)
                {
                    Logger.LogVerbose($"Failed to load {Tiers[level - 1].TextureName}: {ex.Message}");
                    texture = level > 1 ? TrailerTextureFor(level - 1) : null;
                }
                trailerTextures[level] = texture;
            }
            return texture;
        }

        /// <summary>
        /// Fires after <see cref="BusStop.draw"/>. Draws the current trailer hitched behind the bus
        /// (anchored to <see cref="BusStop.busPosition"/> so it follows the drive-off/drive-back
        /// animations), and the construction site while Robin is building the trailer or an upgrade.
        /// </summary>
        private static void BusStopDraw_Postfix(BusStop __instance, SpriteBatch spriteBatch)
        {
            if (IsBuilt && !IsUnderConstruction)
            {
                Texture2D? texture = TrailerTextureFor(Level);
                if (texture == null) return;

                spriteBatch.Draw(
                    texture,
                    Game1.GlobalToLocal(Game1.viewport, __instance.busPosition + TrailerDrawOffset),
                    texture.Bounds,
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
            int tilesWide = Tiers[System.Math.Min(TargetLevel, Tiers.Length) - 1].TilesWide;

            int left = TrailerTileX;
            int top = TrailerTileY;
            int right = TrailerTileX + tilesWide - 1;
            int bottom = TrailerTileY + TrailerTilesHigh - 1;
            int centerX = TrailerTileX + tilesWide / 2;

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
