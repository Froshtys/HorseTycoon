using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Objects;

namespace HorseTycoon.Patches
{
    /// <summary>
    /// Horse mannequin furniture: a tack stand you can hang a saddle set on. Right-clicking one while
    /// holding a saddle equips that set (returning whatever was already on the stand to the player's
    /// inventory) and the matching saddle+bridle sprites are drawn over the mannequin.
    /// <para>The mannequin art was drawn from the horse's side-view sprite, so it reuses the very same
    /// overlay sheets the horses do (<see cref="HorseTexturePatches"/>) rather than needing a second
    /// set of tack art — see <see cref="TackFrame"/> / <see cref="TackOffset"/>.</para>
    /// </summary>
    internal static class MannequinPatches
    {
        internal const string WoodMannequinId = "(F)CP.HorseTycoon.WoodHorseMannequin";
        internal const string ClothMannequinId = "(F)CP.HorseTycoon.ClothHorseMannequin";

        /// <summary>Frame of the shared horse/tack spritesheet the mannequin pose was drawn from: row 1
        /// (side view, facing right), first frame. Confirmed by mask-matching the mannequin art against
        /// every frame of the sheet.</summary>
        private static readonly Rectangle TackFrame = new(0, 32, 32, 32);

        /// <summary>Sprite-pixel nudge that lands that frame's tack on the mannequin: the stand sits to
        /// the lower right of the horse it was traced from, so the tack shifts back up and left to rest
        /// on the plank instead of sinking into it.</summary>
        private static readonly Point TackOffset = new(-1, -1);

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Furniture), nameof(Furniture.checkForAction), new[] { typeof(Farmer), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(MannequinPatches), nameof(Furniture_checkForAction_Prefix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(Furniture), nameof(Furniture.draw), new[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }),
                postfix: new HarmonyMethod(typeof(MannequinPatches), nameof(Furniture_draw_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(Furniture), "loadDescription"),
                postfix: new HarmonyMethod(typeof(MannequinPatches), nameof(Furniture_loadDescription_Postfix))
            );
        }

        /// <summary>Tooltip text for a mannequin. Data/Furniture has no description field — vanilla
        /// <c>loadDescription</c> hands every normal furniture piece a generic line picked from its
        /// placement restriction — so a custom one has to be patched in.</summary>
        private const string MannequinDescription = "Best way to display your tack.";

        internal static bool IsMannequin(Furniture furniture) =>
            furniture.QualifiedItemId == WoodMannequinId || furniture.QualifiedItemId == ClothMannequinId;

        /// <summary>The saddle item id hanging on this mannequin, or null when it's bare. Unlike a horse,
        /// an empty mannequin wears nothing rather than defaulting to brown tack.</summary>
        private static string? GetEquippedSaddleId(Furniture furniture) =>
            furniture.modData.TryGetValue(HorseHelper.EquippedSaddleKey, out string? id)
            && HorseHelper.SaddleItemOverlays.ContainsKey(id)
                ? id
                : null;

        private static bool Furniture_checkForAction_Prefix(Furniture __instance, Farmer who, bool justCheckingForActivity, ref bool __result)
        {
            if (justCheckingForActivity || !IsMannequin(__instance))
                return true;

            if (!HorseHelper.IsSaddleItem(who.ActiveItem))
                return true;

            // The click is ours either way from here on, so nothing else acts on it.
            __result = true;

            string newSaddleId = who.ActiveItem.ItemId;
            string? oldSaddleId = GetEquippedSaddleId(__instance);

            // Re-hanging the set that's already on the stand would just consume one and hand it back.
            if (oldSaddleId == newSaddleId)
                return false;

            // Pop the old set off into the player's inventory (dropped at their feet if it's full).
            if (oldSaddleId != null)
            {
                Item? overflow = who.addItemToInventory(ItemRegistry.Create($"(O){oldSaddleId}"));
                if (overflow != null)
                    Game1.createItemDebris(overflow, who.Position, -1);
            }

            who.reduceActiveItemByOne();
            __instance.modData[HorseHelper.EquippedSaddleKey] = newSaddleId;
            Game1.playSound("dwop");
            return false;
        }

        private static void Furniture_loadDescription_Postfix(Furniture __instance, ref string __result)
        {
            if (IsMannequin(__instance))
                __result = MannequinDescription;
        }

        private static void Furniture_draw_Postfix(Furniture __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
        {
            if (__instance.isTemporarilyInvisible || !IsMannequin(__instance))
                return;

            string? saddleId = GetEquippedSaddleId(__instance);
            if (saddleId == null || !HorseHelper.SaddleItemOverlays.TryGetValue(saddleId, out string? overlays))
                return;

            Rectangle sourceRect = __instance.sourceRect.Value;

            // Same placement math Furniture.draw just used, so the tack tracks the mannequin exactly.
            // The location-furniture branch draws at the protected drawPosition field, which
            // Furniture.updateDrawPosition derives from the bounding box exactly as recomputed here.
            Rectangle bounds = __instance.boundingBox.Value;
            Vector2 origin = Furniture.isDrawingLocationFurniture
                ? new Vector2(bounds.X, bounds.Y - (sourceRect.Height * 4 - bounds.Height))
                : new Vector2(x * 64, y * 64 - (sourceRect.Height * 4 - bounds.Height));

            // A mirrored draw reverses which way "one pixel left" points on screen, so the nudge flips too.
            bool flipped = __instance.Flipped;
            Vector2 position = Game1.GlobalToLocal(Game1.viewport, origin)
                + new Vector2((flipped ? -TackOffset.X : TackOffset.X) * 4, TackOffset.Y * 4);

            // A hair in front of the furniture's own layer (bottom - 8) so the tack is never sorted
            // behind the mannequin, without crossing into the next furniture's band.
            float layerDepth = (bounds.Bottom - 8) / 10000f + 1E-06f;
            SpriteEffects effects = flipped ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

            foreach (string overlayName in overlays.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                Texture2D? texture = HorseTexturePatches.GetOverlayTexture(overlayName.Trim());
                if (texture == null)
                    continue;

                spriteBatch.Draw(texture, position, TackFrame, Color.White * alpha, 0f, Vector2.Zero, 4f, effects, layerDepth);
            }
        }
    }
}
