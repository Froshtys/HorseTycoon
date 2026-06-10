using HarmonyLib;
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

        // Cache skin name → Texture2D (null means file not found, use default)
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
                    priority = Priority.VeryLow // Run after AT (if installed) so our texture wins
                }
            );
        }

        internal static Texture2D? GetTextureForAnimal(FarmAnimal? animal)
        {
            if (animal == null) return null;
            string skinName = animal.skinID.Value switch
            {
                "BlueRoan" => "BlueRoan",
                "Dapple"   => "Dapple",
                "Bay"      => "Bay",
                "Belgian"  => "Belgian",
                "Shire"    => "Shire",
                "Chestnut" => "Chestnut",
                _          => "Roan"
            };
            return GetSkinTexture(skinName);
        }

        internal static void PreloadTextures()
        {
            foreach (string skin in new[] { "Roan", "Bay", "BlueRoan", "Dapple", "Chestnut", "Shire", "Belgian" })
                GetSkinTexture(skin);
        }

        private static readonly Dictionary<string, int> SkinVariation = new()
        {
            ["Roan"]     = 0,
            ["Shire"]    = 1,
            ["Dapple"]   = 2,
            ["Bay"]      = 3,
            ["Belgian"]  = 4,
            ["BlueRoan"] = 5,
            ["Chestnut"] = 6,
        };

        private static Texture2D? GetSkinTexture(string skinName)
        {
            if (_textureCache.TryGetValue(skinName, out Texture2D? cached))
                return cached;

            if (!SkinVariation.TryGetValue(skinName, out int variation))
            {
                _monitor?.Log($"Unknown horse skin '{skinName}', will use default.", LogLevel.Debug);
                _textureCache[skinName] = null;
                return null;
            }

            string fileName = Path.Combine("assets", "horses", $"texture_{variation}_bridal.png");

            try
            {
                var texture = _helper!.ModContent.Load<Texture2D>(fileName);
                _textureCache[skinName] = texture;
                return texture;
            }
            catch
            {
                _monitor?.Log($"No texture file for horse skin '{skinName}' ({fileName}), will use default.", LogLevel.Debug);
                _textureCache[skinName] = null;
                return null;
            }
        }

        private static void Horse_draw_Prefix(Horse __instance)
        {
            if (__instance?.Sprite == null) return;

            if (!__instance.modData.TryGetValue(HorseHelper.HorseSkinKey, out string? skinName))
                return;

            Texture2D? texture = GetSkinTexture(skinName);
            if (texture == null) return;

            // spriteTexture is a public field on AnimatedSprite in SDV 1.6
            __instance.Sprite.spriteTexture = texture;
        }
    }
}
