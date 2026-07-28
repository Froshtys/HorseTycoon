using System;
using System.Collections.Generic;
using System.Linq;
using HorseTycoon.Menus;
using HorseTycoon.Models;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;

namespace HorseTycoon
{
    /// <summary>
    /// Robin-built horse breeding pen. The player assigns a mare and a stallion (right-click the
    /// pen), then feeds each a Gold Carrot. Once both are fed a 3-day timer runs; when it expires
    /// the mare becomes pregnant (via <see cref="BreedingManager"/>) inheriting the penned stallion's
    /// IVs, then the normal 7-day gestation follows. The two horses stand in the middle of the pen
    /// as tackless proxy <see cref="Horse"/> characters playing the grazing animation.
    /// </summary>
    public static class BreedingPenManager
    {
        public const string BuildingId = "Froshty.HorseTycoon_BreedingPen";
        public const string GoldCarrotItemId = "FlashShifter.StardewValleyExpandedCP_Gold_Carrot";

        // --- Pen state (stored on the building's modData; synced net data) ---
        public const string MareIdKey = "Froshty.HorseTycoon/PenMareId";
        public const string StallionIdKey = "Froshty.HorseTycoon/PenStallionId";
        public const string MareFedKey = "Froshty.HorseTycoon/PenMareFed";
        public const string StallionFedKey = "Froshty.HorseTycoon/PenStallionFed";
        public const string BreedDaysLeftKey = "Froshty.HorseTycoon/PenBreedDaysLeft";
        // Set on a penned FarmAnimal so we can find/restore it; value = pen building id string.
        public const string PennedInKey = "Froshty.HorseTycoon/PennedIn";
        // Marks a proxy Horse and ties it to a pen + role: value "&lt;penId&gt;:mare" / "&lt;penId&gt;:stallion".
        public const string PenProxyKey = "Froshty.HorseTycoon/PenProxyOf";

        public const int BreedDelayDays = 3;

        // Purely visual nudge so the proxies sit a bit higher in the pen (toward the back fence)
        // instead of right at the tile's top edge.
        private const float ProxyYOffset = 12f;
        // Purely visual: pushes the mare/stallion a bit farther apart horizontally than their tiles alone.
        private const float ProxyXSpread = 16f;

        // Multiplayer: broadcast a one-shot "chew" so every client animates the fed horse.
        private const string MsgChew = "PenChew";
        private record ChewMessage(string PenId, bool IsMare);

        private static IModHelper Helper = null!;
        private static IMonitor Monitor = null!;

        // Host-only: signature of all pen states last reflected in the proxies. When a farmhand
        // changes a pen (synced building modData), the host notices the signature change and rebuilds.
        private static string LastProxySignature = "";

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            Helper = helper;
            Monitor = monitor;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.SaveLoaded += (_, _) => { if (Context.IsMainPlayer) RefreshProxies(); };
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => { LastProxySignature = ""; };
            helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        }

        // ---------------------------------------------------------------------
        // Pen queries
        // ---------------------------------------------------------------------

        public static bool IsBreedingPen(Building? b) =>
            b != null && b.buildingType.Value == BuildingId && !b.isUnderConstruction();

        private static IEnumerable<Building> AllPens() =>
            Game1.getFarm().buildings.Where(IsBreedingPen);

        private static FarmAnimal? GetPennedAnimal(Building pen, bool mare)
        {
            string key = mare ? MareIdKey : StallionIdKey;
            if (pen.modData.TryGetValue(key, out string idStr) && long.TryParse(idStr, out long id))
                return HorseHelper.GetHiddenHorseById(id);
            return null;
        }

        private static bool IsFed(Building pen, bool mare) =>
            pen.modData.TryGetValue(mare ? MareFedKey : StallionFedKey, out string v) && v == "true";

        // --- Public accessors for the pen menu ---
        public static FarmAnimal? GetMare(Building pen) => GetPennedAnimal(pen, mare: true);
        public static FarmAnimal? GetStallion(Building pen) => GetPennedAnimal(pen, mare: false);
        public static bool IsHorseFed(Building pen, bool mare) => IsFed(pen, mare);
        public static int GetBreedDaysLeft(Building pen) =>
            pen.modData.TryGetValue(BreedDaysLeftKey, out string v) && int.TryParse(v, out int d) ? d : 0;

        // ---------------------------------------------------------------------
        // Assignment (right-click the pen)
        // ---------------------------------------------------------------------

        /// <summary>Opens the two-slot pen menu for choosing the mare and stallion.</summary>
        public static void OpenMenu(Building pen)
        {
            Game1.activeClickableMenu = new BreedingPenMenu(pen);
        }

        /// <summary>
        /// Routes a right-click on the pen: if the player is holding a Gold Carrot and there is a
        /// pair still waiting to be fed, feed one; otherwise open the assignment menu.
        /// </summary>
        public static void HandleActionClick(Building pen, Farmer who)
        {
            bool pairReady = GetPennedAnimal(pen, mare: true) != null && GetPennedAnimal(pen, mare: false) != null;
            bool bothFed = IsFed(pen, mare: true) && IsFed(pen, mare: false);

            if (IsHoldingGoldCarrot(who) && pairReady && !bothFed)
                TryFeed(pen, who);
            else
                OpenMenu(pen);
        }

        /// <summary>Horses eligible to be penned: adult, not a baby, not pregnant, not already penned/hidden.</summary>
        public static List<FarmAnimal> GetEligible(bool wantMale)
        {
            return HorseHelper.GetAllBarnHorses()
                .Where(h => !HorseHelper.IsHidden(h)
                            && !h.isBaby()
                            && !HorseHelper.IsPregnant(h)
                            && h.isMale() == wantMale)
                .OrderBy(h => h.Name)
                .ToList();
        }

        public static void AssignHorse(Building pen, FarmAnimal animal, bool asMare)
        {
            // Free whatever was in this slot first.
            RemoveHorse(pen, asMare, refresh: false);

            pen.modData[asMare ? MareIdKey : StallionIdKey] = animal.myID.Value.ToString();
            animal.modData[HorseHelper.HideKey] = "true";
            animal.modData[PennedInKey] = pen.id.ToString();
            animal.Halt();
            animal.controller = null;

            // A fresh pair must re-feed; cancel any in-progress breeding.
            pen.modData.Remove(BreedDaysLeftKey);

            Logger.LogVerbose($"Assigned {(asMare ? "mare" : "stallion")} '{animal.Name}' ({animal.myID.Value}) to breeding pen {pen.id}.");
            RefreshProxies();
        }

        /// <summary>Un-hides both penned horses and clears pen state (e.g. before the pen is demolished).</summary>
        public static void ReleasePennedHorses(Building pen)
        {
            RemoveHorse(pen, wasMare: true, refresh: false);
            RemoveHorse(pen, wasMare: false, refresh: false);
            RefreshProxies();
        }

        public static void RemoveHorse(Building pen, bool wasMare, bool refresh = true)
        {
            FarmAnimal? animal = GetPennedAnimal(pen, wasMare);
            pen.modData.Remove(wasMare ? MareIdKey : StallionIdKey);
            pen.modData.Remove(wasMare ? MareFedKey : StallionFedKey);
            pen.modData.Remove(BreedDaysLeftKey); // pair broken, cancel breeding countdown

            if (animal != null)
            {
                animal.modData.Remove(PennedInKey);
                HorseHelper.RestoreHorse(animal);
                Logger.LogVerbose($"Removed {(wasMare ? "mare" : "stallion")} '{animal.Name}' from breeding pen {pen.id}.");
            }

            if (refresh) RefreshProxies();
        }

        // ---------------------------------------------------------------------
        // Feeding (hold a Gold Carrot, action-button the pen)
        // ---------------------------------------------------------------------

        /// <summary>True if the player is holding a Gold Carrot they can feed to the pen.</summary>
        public static bool IsHoldingGoldCarrot(Farmer who) =>
            who.CurrentItem != null && who.CurrentItem.ItemId == GoldCarrotItemId;

        /// <summary>Feeds one unfed horse in the pen. Returns true if a carrot was consumed.</summary>
        public static bool TryFeed(Building pen, Farmer who)
        {
            FarmAnimal? mare = GetPennedAnimal(pen, mare: true);
            FarmAnimal? stallion = GetPennedAnimal(pen, mare: false);

            if (mare == null || stallion == null)
            {
                Game1.playSound("cancel");
                Game1.showRedMessage("The pen needs both a mare and a stallion first.");
                return false;
            }

            if (!IsHoldingGoldCarrot(who))
            {
                Game1.playSound("cancel");
                Game1.showRedMessage("Hold a Gold Carrot to feed the horses.");
                return false;
            }

            bool mareFed = IsFed(pen, mare: true);
            bool stallionFed = IsFed(pen, mare: false);
            if (mareFed && stallionFed)
            {
                Game1.playSound("cancel");
                Game1.showRedMessage("Both horses are already fed.");
                return false;
            }

            // Feed the mare first, then the stallion.
            bool feedMare = !mareFed;
            pen.modData[feedMare ? MareFedKey : StallionFedKey] = "true";
            who.reduceActiveItemByOne();
            Game1.playSound("eat");
            PlayChewBroadcast(pen, feedMare);

            // Both fed now → start the breeding countdown.
            if (IsFed(pen, mare: true) && IsFed(pen, mare: false))
            {
                pen.modData[BreedDaysLeftKey] = BreedDelayDays.ToString();
                Game1.showGlobalMessage($"{mare.displayName} and {stallion.displayName} are ready to breed!");
                Logger.LogVerbose($"Breeding pen {pen.id}: both horses fed, pregnancy in {BreedDelayDays} days.");
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage($"Fed {(feedMare ? mare.displayName : stallion.displayName)}.", HUDMessage.achievement_type) { noIcon = true });
            }

            return true;
        }

        // ---------------------------------------------------------------------
        // Daily tick (host only): advance / complete breeding
        // ---------------------------------------------------------------------

        public static void OnDayStarted()
        {
            if (!Context.IsMainPlayer)
                return;

            foreach (Building pen in AllPens().ToList())
            {
                if (!pen.modData.TryGetValue(BreedDaysLeftKey, out string raw) || !int.TryParse(raw, out int daysLeft))
                    continue;

                daysLeft--;
                if (daysLeft > 0)
                {
                    pen.modData[BreedDaysLeftKey] = daysLeft.ToString();
                    continue;
                }

                CompleteBreeding(pen);
            }
        }

        /// <summary>Makes the penned mare pregnant with the penned stallion, then empties the pen.</summary>
        private static void CompleteBreeding(Building pen)
        {
            FarmAnimal? mare = GetPennedAnimal(pen, mare: true);
            FarmAnimal? stallion = GetPennedAnimal(pen, mare: false);

            pen.modData.Remove(BreedDaysLeftKey);

            if (mare == null || stallion == null)
            {
                Logger.LogVerbose($"Breeding pen {pen.id}: a horse went missing before breeding completed; aborting.");
                if (mare != null) ReleaseFromPen(pen, mare: true);
                if (stallion != null) ReleaseFromPen(pen, mare: false);
                return;
            }

            // Seed the sire's IVs so BreedingManager inherits from the penned stallion
            // (bypasses the same-barn sire rule, exactly like the festival stud shop).
            HorseStats sireStats = stallion.GetHorseStats();
            mare.modData[HorseHelper.SireIVsKey] = $"{sireStats.SpeedIV},{sireStats.SprintIV},{sireStats.JumpIV}";

            // Release both back to their barns first, then start the pregnancy on the mare.
            ReleaseFromPen(pen, mare: false);
            ReleaseFromPen(pen, mare: true);

            BreedingManager.MakePregnant(mare); // sends the mare to the birthing area, 7-day gestation
            Game1.showGlobalMessage($"{mare.displayName} is now pregnant! Sired by {stallion.displayName}.");
            Logger.LogVerbose($"Breeding pen {pen.id}: {mare.Name} is pregnant, sired by {stallion.Name}.");
        }

        /// <summary>Clears one slot's pen bookkeeping and un-hides the animal, without cancelling breeding.</summary>
        private static void ReleaseFromPen(Building pen, bool mare)
        {
            FarmAnimal? animal = GetPennedAnimal(pen, mare);
            pen.modData.Remove(mare ? MareIdKey : StallionIdKey);
            pen.modData.Remove(mare ? MareFedKey : StallionFedKey);
            if (animal != null)
            {
                animal.modData.Remove(PennedInKey);
                HorseHelper.RestoreHorse(animal);
            }
            RefreshProxies();
        }

        // ---------------------------------------------------------------------
        // Visual proxy horses (multiplayer)
        //
        // The proxies live in the synced farm characters collection and are spawned ONLY by the
        // host, so they replicate to every farmhand automatically (no per-client duplication).
        // Each client animates the grazing loop locally; the one-shot "chew" on feeding is
        // broadcast so it plays for everyone. The host reconciles proxies whenever pen state
        // (synced building modData) changes, including farmhand-initiated changes.
        // ---------------------------------------------------------------------

        private static void OnSaving(object? sender, SavingEventArgs e)
        {
            // Proxies are transient visuals. Never let them get serialized into the save.
            if (Context.IsMainPlayer)
                DespawnAllProxies();
        }

        private static void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != Helper.ModRegistry.ModID || e.Type != MsgChew)
                return;

            ChewMessage msg = e.ReadAs<ChewMessage>();
            PlayChewOnProxy(msg.PenId, msg.IsMare);
        }

        private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            // Host: rebuild proxies when any pen's state changes (catches farmhand edits once synced).
            if (Context.IsMainPlayer && e.IsMultipleOf(30))
            {
                string sig = ComputePenSignature();
                if (sig != LastProxySignature)
                    RefreshProxies();
            }

            // All clients: keep the grazing loop alive on every pen proxy currently on the farm.
            if (e.IsMultipleOf(15) && Game1.currentLocation is Farm)
            {
                foreach (Horse horse in EnumerateProxies())
                {
                    horse.drawOnTop = true; // not netcode-synced; reassert locally on every client

                    // Vanilla's own carrot-eating overlay (see PlayChewOnProxy) is still running.
                    // Horse.update() decrements this itself, so leave the horse alone until it hits 0.
                    if (GetMunchingCarrotTimer(horse) > 0)
                        continue;

                    if (horse.Sprite?.CurrentAnimation == null)
                        HorseAnimations.SetGrazing(horse, headBobPairs: 2);
                }
            }
        }

        // Vanilla's private Horse.munchingCarrotTimer: when >0, Horse.update()/draw() already draw
        // the correct muzzle-down eating overlay (oriented per FacingDirection) and skip idle logic.
        // We drive that directly instead of hand-rolling frames, so we get the real art, the real
        // pause, and correct left/right orientation for free.
        private static readonly System.Reflection.FieldInfo MunchingCarrotTimerField =
            HarmonyLib.AccessTools.Field(typeof(Horse), "munchingCarrotTimer");

        private static int GetMunchingCarrotTimer(Horse horse) =>
            (int)MunchingCarrotTimerField.GetValue(horse)!;

        private static void SetMunchingCarrotTimer(Horse horse, int milliseconds) =>
            MunchingCarrotTimerField.SetValue(horse, milliseconds);

        private static string ComputePenSignature()
        {
            var parts = new List<string>();
            foreach (Building pen in AllPens())
            {
                pen.modData.TryGetValue(MareIdKey, out string mareId);
                pen.modData.TryGetValue(StallionIdKey, out string stallionId);
                parts.Add($"{pen.id}:{mareId}:{stallionId}");
            }
            parts.Sort();
            return string.Join("|", parts);
        }

        /// <summary>Host-only: despawn and re-create every pen's proxy horses from current state.</summary>
        public static void RefreshProxies()
        {
            if (!Context.IsMainPlayer)
                return;

            Farm farm = Game1.getFarm();
            DespawnAllProxies();

            foreach (Building pen in AllPens())
            {
                FarmAnimal? mare = GetPennedAnimal(pen, mare: true);
                FarmAnimal? stallion = GetPennedAnimal(pen, mare: false);

                // Mare on the left facing right, stallion on the right facing left (facing each other).
                if (mare != null)
                    SpawnProxy(farm, mare, pen, new Vector2(pen.tileX.Value + 2, pen.tileY.Value + 2), Game1.right, isMare: true, xPixelOffset: -ProxyXSpread);
                if (stallion != null)
                    SpawnProxy(farm, stallion, pen, new Vector2(pen.tileX.Value + 3, pen.tileY.Value + 2), Game1.left, isMare: false, xPixelOffset: ProxyXSpread);
            }

            LastProxySignature = ComputePenSignature();
        }

        private static void SpawnProxy(Farm farm, FarmAnimal source, Building pen, Vector2 tile, int facing, bool isMare, float xPixelOffset = 0f)
        {
            var horse = new Horse(Guid.NewGuid(), (int)tile.X, (int)tile.Y)
            {
                Name = source.Name,
                displayName = source.displayName
            };
            HorseHelper.ApplyProxyAppearance(horse, source);
            horse.modData[PenProxyKey] = $"{pen.id}:{(isMare ? "mare" : "stallion")}";
            horse.rider = null;
            horse.controller = null;
            horse.EventActor = false;
            horse.currentLocation = farm;
            horse.Position = tile * 64f - new Vector2(0f, ProxyYOffset) + new Vector2(xPixelOffset, 0f);
            horse.Halt();
            horse.faceDirection(facing);
            // The pen box is fully solid (see breedingpen.json), so the proxy would otherwise
            // sort behind the building's fence texture. drawOnTop forces a fixed high layerDepth
            // (Character.draw) so the horse always renders above the fence. Not a netcode field, so it
            // must also be (re)applied per-client in OnUpdateTicked below.
            horse.drawOnTop = true;
            if (!farm.characters.Contains(horse))
                farm.characters.Add(horse);
            HorseAnimations.SetGrazing(horse, headBobPairs: 2);
        }

        /// <summary>Host-only: remove every pen proxy from the synced farm characters collection.</summary>
        private static void DespawnAllProxies()
        {
            Farm farm = Game1.getFarm();
            foreach (Horse proxy in EnumerateProxies().ToList())
                farm.characters.Remove(proxy);
            LastProxySignature = "";
        }

        /// <summary>All pen proxy horses currently on the farm (identified by their modData flag).</summary>
        private static IEnumerable<Horse> EnumerateProxies() =>
            Game1.getFarm().characters.OfType<Horse>()
                .Where(h => h.modData.ContainsKey(PenProxyKey));

        // ---------------------------------------------------------------------
        // Chew animation (broadcast so it plays on every client)
        // ---------------------------------------------------------------------

        private static void PlayChewBroadcast(Building pen, bool mare)
        {
            PlayChewOnProxy(pen.id.ToString(), mare);
            Helper.Multiplayer.SendMessage(
                new ChewMessage(pen.id.ToString(), mare), MsgChew,
                modIDs: new[] { Helper.ModRegistry.ModID });
        }

        private static void PlayChewOnProxy(string penId, bool mare)
        {
            string tag = $"{penId}:{(mare ? "mare" : "stallion")}";
            Horse? horse = Game1.getFarm().characters.OfType<Horse>()
                .FirstOrDefault(h => h.modData.TryGetValue(PenProxyKey, out string v) && v == tag);
            if (horse?.Sprite == null) return;

            // Stop the grazing loop and hand off to vanilla's own carrot-eating overlay (see
            // GetMunchingCarrotTimer). It draws a dedicated muzzle-down sprite oriented by
            // FacingDirection, so the mare (facing right) and stallion (facing left) each get the
            // correctly mirrored art automatically, and Horse.update() skips idle animation
            // while it's active, so there's no need to fight the grazing loop ourselves.
            horse.Sprite.StopAnimation();
            SetMunchingCarrotTimer(horse, 1500);
            PlayHeartAbove(horse);
        }

        // doEmote's heart draws via Character.DrawEmote, which sorts by StandingPixel.Y and ignores
        // drawOnTop, so it rendered behind the (fully solid) pen building. A manually-placed TAS lets us
        // force a layerDepth that's always above the building instead.
        private static void PlayHeartAbove(Horse horse)
        {
            GameLocation? location = horse.currentLocation;
            if (location == null) return;

            Vector2 pos = new(
                horse.Position.X + horse.Sprite.SpriteWidth * 4f / 2f - 32f,
                horse.Position.Y - 96f);

            var heartTas = new TemporaryAnimatedSprite("TileSheets\\emotes", new Rectangle(0, 80, 16, 16), 250f, 4, 2, pos, flicker: false, flipped: false)
            {
                layerDepth = 1f,
                scale = 4f
            };
            var introTas = new TemporaryAnimatedSprite("TileSheets\\emotes", new Rectangle(0, 0, 16, 16), 20f, 4, 0, pos, flicker: false, flipped: false)
            {
                layerDepth = 1f,
                scale = 4f,
                endFunction = _ => location.temporarySprites.Add(heartTas)
            };
            location.temporarySprites.Add(introTas);
        }

    }
}
