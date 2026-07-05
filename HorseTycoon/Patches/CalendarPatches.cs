using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace HorseTycoon.Patches
{
    /// <summary>
    /// Draws a horse-face marker on the Billboard calendar for every registered horse festival.
    ///
    /// The at-home races (Spring 19 / Fall 19) ARE registered in Data/Festivals/FestivalDates, so vanilla
    /// already draws its animated festival flag on them — we swap that for the horse icon. The away race
    /// (Summer 19) is deliberately NOT in FestivalDates (so it doesn't close the town, like the Desert
    /// Festival), which means vanilla shows nothing there — so we add the marker ourselves. In both cases
    /// the icon is drawn in <see cref="Billboard.draw"/> so it can coexist with a birthday portrait sharing
    /// the same day (e.g. Demetrius' birthday is Summer 19).
    /// </summary>
    internal static class CalendarPatches
    {
        /// <summary>The mod's horse icon (loaded from the CP content pack); the left 16x16 frame is the head.</summary>
        private const string HorseIconAsset = "CP.HorseTycoon/HorseIcon";
        private static readonly Rectangle IconSourceRect = new(0, 0, 16, 16);
        private static Texture2D? _icon;

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Billboard), nameof(Billboard.GetEventsForDay)),
                postfix: new HarmonyMethod(typeof(CalendarPatches), nameof(GetEventsForDay_Postfix)));

            harmony.Patch(
                original: AccessTools.Method(typeof(Billboard), nameof(Billboard.draw), new[] { typeof(SpriteBatch) }),
                postfix: new HarmonyMethod(typeof(CalendarPatches), nameof(Draw_Postfix)));
        }

        /// <summary>The horse festival for the given calendar day in the current season, or null.</summary>
        private static (string Season, int Day, string AssetKey)? FestivalForDay(int day)
        {
            string season = Game1.currentSeason;
            foreach (var fest in FestivalRaceManager.CalendarFestivals)
            {
                if (fest.Season == season && fest.Day == day)
                    return fest;
            }
            return null;
        }

        /// <summary>
        /// Replaces the vanilla festival flag entry on horse-festival days with a nameless marker that only
        /// carries the festival name (for the hover tooltip). We keep it texture-less so it doesn't claim the
        /// day's single icon slot away from a birthday portrait; the horse icon itself is drawn in <see cref="Draw_Postfix"/>.
        /// </summary>
        private static void GetEventsForDay_Postfix(int day, List<Billboard.BillboardEvent> __result)
        {
            var fest = FestivalForDay(day);
            if (fest == null)
                return;

            // Drop the vanilla festival flag (present for the FestivalDates-registered Spring/Fall races)
            // so the day shows only our horse marker.
            __result.RemoveAll(e => e.Type == Billboard.BillboardEventType.Festival);

            string name = GetFestivalName(fest.Value.AssetKey) ?? "Horse Festival";
            __result.Add(new Billboard.BillboardEvent(Billboard.BillboardEventType.None, System.Array.Empty<string>(), name));
        }

        /// <summary>Draws the horse icon on each horse-festival day slot of the currently shown month.</summary>
        private static void Draw_Postfix(Billboard __instance, SpriteBatch b)
        {
            // calendarDays is only populated in calendar mode (null on the daily-quest board).
            List<ClickableTextureComponent>? days = __instance.calendarDays;
            if (days == null)
                return;

            Texture2D? icon = GetIcon();
            if (icon == null)
                return;

            foreach (ClickableTextureComponent slot in days)
            {
                if (FestivalForDay(slot.myID) == null)
                    continue;

                // Same slot the vanilla festival flag uses, so it reads as the festival marker and sits
                // clear of a birthday portrait (drawn higher, at +48/+28).
                Utility.drawWithShadow(
                    b, icon,
                    new Vector2(slot.bounds.X + 36, slot.bounds.Y + 52),
                    IconSourceRect, Color.White, 0f, Vector2.Zero, 4f, flipped: false, 1f);
            }
        }

        private static Texture2D? GetIcon()
        {
            if (_icon == null)
            {
                try { _icon = Game1.content.Load<Texture2D>(HorseIconAsset); }
                catch { _icon = null; }
            }
            return _icon;
        }

        private static string? GetFestivalName(string assetKey)
        {
            try
            {
                var data = Game1.temporaryContent.Load<Dictionary<string, string>>("Data\\Festivals\\" + assetKey);
                return data != null && data.TryGetValue("name", out string? name) ? name : null;
            }
            catch { return null; }
        }
    }
}
