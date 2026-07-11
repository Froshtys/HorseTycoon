using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

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

        /// <summary>Trailer-local pixel position of the first transparent window on every tier sprite,
        /// and how far the second window sits in from the sprite's right edge. Windows are
        /// <see cref="WindowWidth"/> x <see cref="WindowHeight"/> holes the loaded horses' heads show through.</summary>
        private const int Window1X = 26;
        private const int Window2FromRight = 45;
        private const int WindowY = 29;
        private const int WindowWidth = 13;
        private const int WindowHeight = 9;

        /// <summary>The slice of the horse sheet's right-facing stand frame (frame 7, at 0,32) that lands
        /// in a window when the head is lined up with it: the head spans rows 5-13, x 17-29 of the frame.
        /// Drawn with <see cref="SpriteEffects.FlipHorizontally"/> so the horse faces left, toward the bus.</summary>
        private static readonly Rectangle HorseHeadSource = new(17, 32 + 5, WindowWidth, WindowHeight);

        private static IModHelper helper = null!;
        private static readonly Dictionary<int, Texture2D?> trailerTextures = new();
        private static readonly Dictionary<long, Texture2D> loadedHorseTextures = new();

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

        /// <summary>A farmhand bought a trailer tier from Robin; the host starts the construction.</summary>
        private const string MsgTrailerPurchase = "BusTrailerPurchase";
        private record TrailerPurchaseMessage(int TargetLevel);

        /// <summary>Construction finished (host-side countdown); farmhands show the completion toast.</summary>
        private const string MsgTrailerBuilt = "BusTrailerBuilt";
        private record TrailerBuiltMessage(string TrailerName);

        public static void Initialize(IModHelper modHelper)
        {
            helper = modHelper;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;

            var harmony = new Harmony("Froshty.HorseTycoon.BusTrailer");

            // Inject the trailer option into Robin's carpenter question menu. Patched on carpenters()
            // itself (not createQuestionDialogue) because createQuestionDialogue is small/hot enough
            // that the JIT inlines it into callers like carpenters(), which silently defeats a prefix
            // patch on it (Harmony.GetPatchInfo still reports the patch as attached, but the redirect
            // is never hit at that call site). carpenters() is rare/large enough to stay un-inlined.
            PatchOrLog(harmony, "carpenters", () => harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.carpenters)),
                postfix: new HarmonyMethod(typeof(BusTrailerManager), nameof(Carpenters_Postfix))));

            // Handle the injected menu option and the follow-up yes/no answer.
            PatchOrLog(harmony, "answerDialogueAction", () => harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.answerDialogueAction)),
                prefix: new HarmonyMethod(typeof(BusTrailerManager), nameof(AnswerDialogueAction_Prefix))));

            // Draw the finished trailer / construction site behind the bus.
            PatchOrLog(harmony, "BusStop.draw", () => harmony.Patch(
                original: AccessTools.Method(typeof(BusStop), nameof(BusStop.draw), new[] { typeof(SpriteBatch) }),
                postfix: new HarmonyMethod(typeof(BusTrailerManager), nameof(BusStopDraw_Postfix))));

            // Block walking through the trailer's footprint (both mid-construction and once built).
            // This is the widest isCollidingPosition overload; the other overloads all funnel into it,
            // so a single postfix here covers farmer movement, pathfinding, etc.
            PatchOrLog(harmony, "isCollidingPosition", () => harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.isCollidingPosition), new[]
                {
                    typeof(Rectangle), typeof(xTile.Dimensions.Rectangle), typeof(bool), typeof(int), typeof(bool),
                    typeof(Character), typeof(bool), typeof(bool), typeof(bool), typeof(bool)
                }),
                postfix: new HarmonyMethod(typeof(BusTrailerManager), nameof(IsCollidingPosition_Postfix))));
        }

        /// <summary>Applies a Harmony patch and logs whether it actually succeeded, instead of letting a
        /// failure surface only as a missing effect later (used to diagnose a patch silently not firing).</summary>
        private static void PatchOrLog(Harmony harmony, string label, Action patch)
        {
            try
            {
                patch();
                Logger.LogVerbose($"BusTrailerManager: patched {label} successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"BusTrailerManager: FAILED to patch {label}: {ex}");
            }
        }

        /// <summary>Host-side construction countdown, mirroring how house upgrades tick down day by day.</summary>
        private static void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            loadedHorseTextures.Clear();

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
                helper.Multiplayer.SendMessage(
                    new TrailerBuiltMessage(name),
                    MsgTrailerBuilt,
                    modIDs: new[] { helper.ModRegistry.ModID });
                Logger.LogVerbose($"Bus horse trailer construction complete (level {level}).");
            }
            else
            {
                farm.modData[DaysLeftKey] = days.ToString();
            }
        }

        /// <summary>Stamps the construction countdown into farm modData. Host-only: farm modData is
        /// synced from the host, so farmhand purchases arrive via <see cref="MsgTrailerPurchase"/>.</summary>
        private static void StartConstruction(int targetLevel)
        {
            Farm farm = Game1.getFarm();
            farm.modData[DaysLeftKey] = BuildDays.ToString();
            farm.modData[TargetLevelKey] = targetLevel.ToString();
        }

        private static void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != helper.ModRegistry.ModID)
                return;

            if (e.Type == MsgTrailerBuilt)
            {
                Game1.showGlobalMessage($"Robin has finished the {e.ReadAs<TrailerBuiltMessage>().TrailerName} at the bus stop.");
                return;
            }

            if (e.Type != MsgTrailerPurchase || !Context.IsMainPlayer)
                return;

            int targetLevel = e.ReadAs<TrailerPurchaseMessage>().TargetLevel;
            if (IsUnderConstruction || targetLevel != Level + 1)
            {
                Logger.LogVerbose($"Ignoring stale trailer purchase message (target {targetLevel}, current level {Level}, underConstruction={IsUnderConstruction}).");
                return;
            }
            StartConstruction(targetLevel);
            Logger.LogVerbose($"Farmhand trailer purchase applied: level {targetLevel} under construction.");
        }

        // ======================== Robin's carpenter menu ========================

        /// <summary>Whether Robin should currently offer the next trailer tier. Any player can buy it
        /// (it's one shared farm-wide trailer, like the vanilla community upgrade), gated on the bus
        /// being repaired and on someone on the farm having won the Spring Horse Festival (which earns
        /// Lewis's scouting letter).</summary>
        private static bool ShouldOfferTrailer()
        {
            bool worldReady = Context.IsWorldReady;
            bool hasCcVault = worldReady && Game1.MasterPlayer.mailReceived.Contains("ccVault");
            bool wonSpringFestival = worldReady && FestivalRaceManager.HasAnyPlayerWonSpringFestival();
            TrailerTier? nextTier = worldReady ? NextTier : null;
            bool underConstruction = worldReady && IsUnderConstruction;

            Logger.LogVerbose($"ShouldOfferTrailer: worldReady={worldReady}, hasCcVault={hasCcVault}, "
                + $"wonSpringFestival={wonSpringFestival}, level={(worldReady ? Level : -1)}, "
                + $"nextTier={nextTier?.Name ?? "null"}, underConstruction={underConstruction}");

            return worldReady
                && hasCcVault
                && wonSpringFestival
                && nextTier != null
                && !underConstruction;
        }

        /// <summary>
        /// Fires after <see cref="GameLocation.carpenters(Location)"/>. If vanilla just built and showed
        /// the "carpenter" question dialogue, mutates the already-constructed <see cref="DialogueBox"/>'s
        /// response list directly to insert the trailer option just above "Leave" (rather than patching
        /// <c>createQuestionDialogue</c> itself, since that call gets JIT-inlined into this caller).
        /// </summary>
        private static void Carpenters_Postfix(GameLocation __instance)
        {
            if (!ShouldOfferTrailer())
                return;
            if (__instance.lastQuestionKey != "carpenter")
                return;
            if (Game1.activeClickableMenu is not DialogueBox dialogueBox || !dialogueBox.isQuestion)
                return;

            string label = Level == 0
                ? $"Build a {NextTier!.Name}"
                : $"Upgrade to a {NextTier!.Name}";

            var choices = dialogueBox.responses.ToList();
            int leaveIndex = choices.FindIndex(r => r.responseKey == "Leave");
            if (leaveIndex < 0) leaveIndex = choices.Count;
            choices.Insert(leaveIndex, new Response("HorseTrailer", label));
            dialogueBox.responses = choices.ToArray();

            // The box's height/position were already computed (by the constructor) for the original,
            // shorter response list. Recompute them the same way the constructor does, or the added
            // response overflows past the bottom edge instead of the box growing to fit it.
            helper.Reflection.GetMethod(dialogueBox, "setUpQuestions").Invoke();
            dialogueBox.height = dialogueBox.heightForQuestions;
            dialogueBox.x = (int)Utility.getTopLeftPositionForCenteringOnScreen(dialogueBox.width, dialogueBox.height).X;
            dialogueBox.y = Game1.uiViewport.Height - dialogueBox.height - 64;
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
                ? $"I can build you a horse trailer for the bus. With that hitched up, you could bring your horses to festivals out of town. It'll cost {price}g plus {tier.BarCount} {tier.BarName}s and take {BuildDays} days. Want me to get started?"
                : $"I could upgrade that trailer into a {tier.Name.ToLower()}, room for {tier.HorseCapacity} horses! It'll cost {price}g plus {tier.BarCount} {tier.BarName}s and take {BuildDays} days. Want me to get started?";

            location.createQuestionDialogue(
                Game1.parseText(pitch),
                location.createYesNoResponses(),
                "busTrailer");
        }

        private static void AcceptTrailer()
        {
            TrailerTier? tier = NextTier;
            if (tier == null) return;

            // Re-check in case another player started this build while the menu was open.
            if (IsUnderConstruction)
            {
                Game1.drawObjectDialogue("Robin is already working on the trailer.");
                return;
            }
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
            if (Context.IsMainPlayer)
            {
                StartConstruction(Level + 1);
            }
            else
            {
                // Farmhands can't reliably write Farm modData from Robin's shop (locations only
                // sync while a player is inside), so the host applies the construction state.
                helper.Multiplayer.SendMessage(
                    new TrailerPurchaseMessage(Level + 1),
                    MsgTrailerPurchase,
                    modIDs: new[] { helper.ModRegistry.ModID });
            }

            NPC? robin = Game1.getCharacterFromName("Robin");
            if (robin != null)
            {
                robin.setNewDialogue(new Dialogue(robin, null, $"You got it! I'll head down to the bus stop and get started. Give me {BuildDays} days and you'll be hauling horses all over the valley!$h"));
                Game1.drawDialogue(robin);
            }
            Logger.LogVerbose($"{tier.Name} purchased for {tier.Price}g + {tier.BarCount} {tier.BarName}s; {BuildDays} days of construction.");
        }

        /// <summary>
        /// Called from the <c>BusStop.busLeftToDesert</c> prefix when the drive-off animation thinks the
        /// bus has left the screen. Vanilla fires the departure warp as soon as the bus body clears the
        /// west edge (<c>busPosition.X + 512 &lt; 10</c>), but the trailer hitched 512px behind it is
        /// still mid-screen at that point — so this un-sets the private <c>leaving</c> flag, which makes
        /// <c>UpdateWhenCurrentLocation</c> keep the bus accelerating and re-fire <c>busLeftToDesert</c>
        /// each tick until the trailer's right edge has cleared the screen too. Returns true while the
        /// departure should be held back.
        /// </summary>
        public static bool TryDelayDepartureForTrailer(BusStop busStop)
        {
            if (!IsBuilt || IsUnderConstruction)
                return false;

            Texture2D? texture = TrailerTextureFor(Level);
            if (texture == null)
                return false;

            float trailerRightEdge = busStop.busPosition.X + TrailerDrawOffset.X + texture.Bounds.Width * 4f;
            if (trailerRightEdge < 10f)
                return false;

            helper.Reflection.GetField<bool>(busStop, "leaving").SetValue(false);
            return true;
        }

        // ======================== Collision ========================

        /// <summary>The trailer's pixel footprint behind the bus (construction site or finished trailer,
        /// whichever is current), or null when there's no trailer and none is being built. The footprint
        /// is anchored at a fixed tile regardless of the bus's drive-off/arrival animation, matching
        /// <see cref="DrawConstructionSite"/>'s tile math. The front row (nearest the bus) is left out so
        /// players can walk behind the trailer; the construction site is one row shorter still, since the
        /// framing hasn't gone up yet.</summary>
        private static Rectangle? GetFootprint()
        {
            if (!Context.IsWorldReady || (!IsBuilt && !IsUnderConstruction))
                return null;

            int level = IsUnderConstruction ? TargetLevel : Level;
            int tilesWide = Tiers[System.Math.Min(level, Tiers.Length) - 1].TilesWide;
            int blockedRows = TrailerTilesHigh - 1 - (IsUnderConstruction ? 1 : 0);
            return new Rectangle(TrailerTileX * 64, (TrailerTileY + 1) * 64, tilesWide * 64, blockedRows * 64);
        }

        /// <summary>Fires after <see cref="GameLocation.isCollidingPosition"/>. Blocks movement into the
        /// trailer's footprint at the Bus Stop, since it's drawn/tracked purely by this manager rather
        /// than as a real <see cref="StardewValley.Buildings.Building"/> with its own collision box.</summary>
        private static void IsCollidingPosition_Postfix(GameLocation __instance, Rectangle position, ref bool __result)
        {
            if (__result || __instance is not BusStop)
                return;

            Rectangle? footprint = GetFootprint();
            if (footprint.HasValue && position.Intersects(footprint.Value))
                __result = true;
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
                DrawTrailerBehindBus(spriteBatch, __instance.busPosition, (__instance.busPosition.Y + 192f) / 10000f);
            }
            else if (IsUnderConstruction)
            {
                DrawConstructionSite(spriteBatch);
            }
        }

        /// <summary>
        /// Draws the current trailer (with any loaded horses' heads in its windows) hitched behind a bus
        /// whose body's top-left is at <paramref name="busGlobalPosition"/>. Used by the Bus Stop draw
        /// patch and by the festival arrival cinematic's mod-drawn bus, so the trailer that drove off
        /// with the player also arrives with them. No-op without a finished trailer.
        /// </summary>
        public static void DrawTrailerBehindBus(SpriteBatch spriteBatch, Vector2 busGlobalPosition, float layerDepth)
        {
            if (!IsBuilt || IsUnderConstruction)
                return;
            Texture2D? texture = TrailerTextureFor(Level);
            if (texture == null)
                return;

            Vector2 trailerGlobalPosition = busGlobalPosition + TrailerDrawOffset;
            spriteBatch.Draw(
                texture,
                Game1.GlobalToLocal(Game1.viewport, trailerGlobalPosition),
                texture.Bounds,
                Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None,
                layerDepth);

            DrawLoadedHorseHeads(spriteBatch, trailerGlobalPosition, texture, layerDepth);
        }

        /// <summary>
        /// Draws the heads of the horses loaded onto the bus (any player's selection, local player's
        /// first) behind the trailer's two transparent windows, so they peek out while the bus waits
        /// and during the drive-off/arrival animations. Each window shows one horse; extra horses
        /// beyond the two windows simply aren't visible. Only the window-sized head slice is drawn,
        /// so nothing can leak outside the trailer art.
        /// </summary>
        private static void DrawLoadedHorseHeads(SpriteBatch spriteBatch, Vector2 trailerGlobalPosition, Texture2D trailerTexture, float trailerDepth)
        {
            List<long> loadedIds = FestivalRaceManager.GetBusTrailerHorseIds();
            if (loadedIds.Count == 0) return;

            int[] windowX = { Window1X, trailerTexture.Bounds.Width - Window2FromRight };
            int window = 0;
            foreach (long animalId in loadedIds)
            {
                if (window >= windowX.Length) break;
                Texture2D? horseTexture = HorseTextureFor(animalId);
                if (horseTexture == null) continue;

                spriteBatch.Draw(
                    horseTexture,
                    Game1.GlobalToLocal(Game1.viewport,
                        trailerGlobalPosition + new Vector2(windowX[window] * 4f, WindowY * 4f)),
                    HorseHeadSource,
                    Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.FlipHorizontally,
                    trailerDepth - 1E-05f);
                window++;
            }
        }

        /// <summary>The sprite sheet for a loaded horse, resolved from its barn FarmAnimal and cached
        /// for the day (the cache is cleared at day start alongside the bus claims).</summary>
        private static Texture2D? HorseTextureFor(long animalId)
        {
            if (loadedHorseTextures.TryGetValue(animalId, out Texture2D? texture))
                return texture;

            FarmAnimal? animal = HorseHelper.GetAllBarnHorses().FirstOrDefault(a => a.myID.Value == animalId);
            texture = animal?.Sprite?.Texture;
            if (texture != null)
                loadedHorseTextures[animalId] = texture;
            return texture;
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
