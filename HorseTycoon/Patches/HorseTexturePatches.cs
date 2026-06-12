using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon.Patches
{
    internal static class HorseTexturePatches
    {
        private static IModHelper? _helper;
        private static IMonitor? _monitor;

        // name → texture for every PNG in assets/horse_overlays/
        private static readonly Dictionary<string, Texture2D> _overlayDict = new();

        // "skinName|Overlay1,Overlay2" → composited Texture2D
        private static readonly Dictionary<string, Texture2D?> _textureCache = new();

        internal static void Initialize(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;
        }

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Horse), nameof(Horse.draw), new[] { typeof(SpriteBatch) }),
                prefix: new HarmonyMethod(typeof(HorseTexturePatches), nameof(Horse_draw_Prefix))
                {
                    priority = Priority.VeryLow
                }
            );
        }

        internal static IReadOnlyCollection<string> AvailableOverlayNames => _overlayDict.Keys;


        internal static Texture2D? GetTextureForAnimal(FarmAnimal? animal)
        {
            if (animal == null) return null;
            string skinName = SkinNameFromId(animal.skinID.Value);
            string? overlaysValue = HorseHelper.GetOverlaysRaw(animal);
            return GetSkinTexture(skinName, overlaysValue);
        }

        internal static void PreloadTextures()
        {
            _textureCache.Clear();
            LoadOverlays();
            foreach (string skin in new[] { "Roan", "Bay", "BlueRoan", "Dapple", "Chestnut", "Shire", "Belgian" })
                GetSkinTexture(skin, null);
        }

        private static void LoadOverlays()
        {
            _overlayDict.Clear();
            string overlayDir = Path.Combine(_helper!.DirectoryPath, "assets", "horse_overlays");
            if (!Directory.Exists(overlayDir)) return;

            foreach (string fullPath in Directory.GetFiles(overlayDir, "*.png").OrderBy(f => f))
            {
                string name = Path.GetFileNameWithoutExtension(fullPath);
                string relativePath = Path.GetRelativePath(_helper.DirectoryPath, fullPath);
                try
                {
                    _overlayDict[name] = _helper.ModContent.Load<Texture2D>(relativePath);
                    _monitor?.Log($"Loaded overlay: {name}", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    _monitor?.Log($"Failed to load overlay '{name}': {ex.Message}", LogLevel.Warn);
                }
            }
        }

        private static readonly Dictionary<string, int> SkinVariation = new()
        {
            ["Roan"] = 0,
            ["Shire"] = 1,
            ["Dapple"] = 2,
            ["Bay"] = 3,
            ["Belgian"] = 4,
            ["BlueRoan"] = 5,
            ["Chestnut"] = 6,
        };

        private static string SkinNameFromId(string? skinId) => skinId switch
        {
            "BlueRoan" => "BlueRoan",
            "Dapple" => "Dapple",
            "Bay" => "Bay",
            "Belgian" => "Belgian",
            "Shire" => "Shire",
            "Chestnut" => "Chestnut",
            _ => "Roan"
        };

        /// <summary>
        /// Resolves the overlay name list from a raw modData string.
        /// Null or empty → no overlays.
        /// </summary>
        private static string[] ResolveOverlays(string? overlaysValue)
        {
            if (string.IsNullOrWhiteSpace(overlaysValue))
                return Array.Empty<string>();

            return overlaysValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => _overlayDict.ContainsKey(s))
                .OrderBy(s => s)
                .ToArray();
        }

        private static string MakeCacheKey(string skinName, string[] overlays) =>
            overlays.Length == 0 ? skinName : $"{skinName}|{string.Join(",", overlays)}";

        /// <param name="overlaysValue">Raw modData overlay string, or null for "use all".</param>
        private static Texture2D? GetSkinTexture(string skinName, string? overlaysValue)
        {
            string[] overlays = ResolveOverlays(overlaysValue);
            string key = MakeCacheKey(skinName, overlays);

            if (_textureCache.TryGetValue(key, out Texture2D? cached))
                return cached;

            if (!SkinVariation.TryGetValue(skinName, out int variation))
            {
                _monitor?.Log($"Unknown horse skin '{skinName}', will use default.", LogLevel.Debug);
                _textureCache[key] = null;
                return null;
            }

            string fileName = Path.Combine("assets", "horses", $"texture_{variation}.png");
            Texture2D? baseTexture;
            try
            {
                baseTexture = _helper!.ModContent.Load<Texture2D>(fileName);
            }
            catch
            {
                _monitor?.Log($"No texture for skin '{skinName}' ({fileName}).", LogLevel.Debug);
                _textureCache[key] = null;
                return null;
            }

            var result = overlays.Length > 0 ? ComposeWithOverlays(baseTexture, overlays) : baseTexture;
            _textureCache[key] = result;
            return result;
        }

        private static Texture2D ComposeWithOverlays(Texture2D baseTexture, string[] overlayNames)
        {
            int w = baseTexture.Width, h = baseTexture.Height;
            Color[] pixels = new Color[w * h];
            baseTexture.GetData(pixels);

            Color[] overlayPixels = new Color[w * h];
            foreach (string name in overlayNames)
            {
                if (!_overlayDict.TryGetValue(name, out Texture2D? overlay)) continue;

                if (overlay.Width != w || overlay.Height != h)
                {
                    _monitor?.Log($"Overlay '{name}' size {overlay.Width}x{overlay.Height} doesn't match base {w}x{h}, skipping.", LogLevel.Warn);
                    continue;
                }

                overlay.GetData(overlayPixels);
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color o = overlayPixels[i];
                    if (o.A == 0) continue;

                    float invA = 1f - o.A / 255f;
                    Color b = pixels[i];
                    pixels[i] = new Color(
                        (byte)Math.Min(255, o.R + (int)(b.R * invA)),
                        (byte)Math.Min(255, o.G + (int)(b.G * invA)),
                        (byte)Math.Min(255, o.B + (int)(b.B * invA)),
                        (byte)Math.Min(255, o.A + (int)(b.A * invA))
                    );
                }
            }

            var result = new Texture2D(Game1.graphics.GraphicsDevice, w, h);
            result.SetData(pixels);
            return result;
        }

        private static void Horse_draw_Prefix(Horse __instance)
        {
            if (__instance?.Sprite == null) return;

            if (!__instance.modData.TryGetValue(HorseHelper.HorseSkinKey, out string? skinName))
                return;

            __instance.modData.TryGetValue(HorseHelper.OverlaysKey, out string? overlaysValue);

            Texture2D? texture = GetSkinTexture(skinName, overlaysValue);
            if (texture == null) return;

            __instance.Sprite.spriteTexture = texture;
        }
    }
}
