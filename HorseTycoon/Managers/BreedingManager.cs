using HorseTycoon.Models;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;

namespace HorseTycoon
{
    /// <summary>
    /// Horse pregnancy lifecycle: replaces the instant vanilla barn birth with a 7-day
    /// gestation. The pregnant mare rests at the bottom-left of her home barn until the
    /// foal arrives.
    /// </summary>
    public static class BreedingManager
    {
        public const int GestationDays = 7;

        /// <summary>
        /// Adult male horses sharing the mare's home barn — only these can sire a barn pregnancy.
        /// Stable-hidden males count (hiding doesn't change their home barn).
        /// </summary>
        public static List<FarmAnimal> GetEligibleSires(FarmAnimal mare)
        {
            if (mare.home == null)
                return new List<FarmAnimal>();

            return HorseHelper.GetAllBarnHorses()
                .Where(h => h.myID.Value != mare.myID.Value &&
                            h.isMale() &&
                            !h.isBaby() &&
                            h.home == mare.home)
                .ToList();
        }

        public static bool HasEligibleSire(FarmAnimal mare)
        {
            return GetEligibleSires(mare).Count > 0;
        }

        /// <summary>
        /// Marks a mare as pregnant and sends her to the birthing area. Unless a sire is already
        /// recorded (festival Stud Shop path), a specific stallion is chosen from her home barn
        /// and his IVs are saved on the mare for inheritance at birth.
        /// </summary>
        /// <returns>The barn stallion chosen as sire, or null (stud-shop sire or none available).</returns>
        public static FarmAnimal? MakePregnant(FarmAnimal mare)
        {
            FarmAnimal? sire = null;
            if (!mare.modData.ContainsKey(HorseHelper.SireIVsKey))
            {
                List<FarmAnimal> candidates = GetEligibleSires(mare);
                if (candidates.Count > 0)
                {
                    sire = candidates[Game1.random.Next(candidates.Count)];
                    HorseStats sireStats = sire.GetHorseStats();
                    mare.modData[HorseHelper.SireIVsKey] = $"{sireStats.SpeedIV},{sireStats.SprintIV},{sireStats.JumpIV}";
                }
            }

            mare.modData[HorseHelper.PregnancyDaysLeftKey] = GestationDays.ToString();
            SendToBirthingArea(mare);
            Logger.LogVerbose($"{mare.Name} ({mare.myID.Value}) is now pregnant, due in {GestationDays} days." +
                (sire != null ? $" Sired by {sire.Name} ({sire.myID.Value})." : " No barn sire recorded."));
            return sire;
        }

        /// <summary>Host-only daily tick: advance every pregnancy, delivering foals that are due.</summary>
        public static void OnDayStarted()
        {
            List<FarmAnimal> dueMares = new();

            foreach (FarmAnimal mare in HorseHelper.GetAllBarnHorses())
            {
                if (!HorseHelper.IsPregnant(mare))
                    continue;

                int daysLeft = HorseHelper.GetPregnancyDaysLeft(mare) - 1;
                if (daysLeft > 0)
                {
                    mare.modData[HorseHelper.PregnancyDaysLeftKey] = daysLeft.ToString();
                    SendToBirthingArea(mare);
                    Logger.LogVerbose($"{mare.Name} is pregnant: {daysLeft} day(s) until birth.");
                }
                else
                {
                    dueMares.Add(mare);
                }
            }

            DeliverFoals(dueMares, 0);
        }

        /// <summary>Parks the mare at the bottom-left corner of her home barn and stops her wandering.</summary>
        public static void SendToBirthingArea(FarmAnimal mare)
        {
            if (mare.home?.GetIndoors() is not AnimalHouse interior)
                return;

            // Make sure the mare is registered indoors (mirrors HorseHelper.RestoreHorse).
            if (mare.currentLocation != interior)
            {
                mare.currentLocation?.animals.Remove(mare.myID.Value);
                if (!interior.animals.ContainsKey(mare.myID.Value))
                    interior.animals.Add(mare.myID.Value, mare);
                mare.currentLocation = interior;
            }

            int mapHeight = interior.map.Layers[0].LayerHeight;
            Vector2 restTile = Utility.recursiveFindOpenTileForCharacter(mare, interior, new Vector2(2, mapHeight - 4), 12);
            mare.Position = restTile * 64f;
            mare.Halt();
            mare.FacingDirection = 2;
            mare.controller = null;
        }

        /// <summary>Delivers foals one at a time so each gets its own naming menu.</summary>
        private static void DeliverFoals(List<FarmAnimal> dueMares, int index)
        {
            if (index >= dueMares.Count)
                return;

            FarmAnimal mare = dueMares[index];
            if (mare.home?.GetIndoors() is not AnimalHouse interior)
            {
                // Home barn is gone — postpone one day rather than losing the foal.
                mare.modData[HorseHelper.PregnancyDaysLeftKey] = "1";
                DeliverFoals(dueMares, index + 1);
                return;
            }

            mare.modData.Remove(HorseHelper.PregnancyDaysLeftKey);
            string? sireIVsRaw = mare.modData.TryGetValue(HorseHelper.SireIVsKey, out string sireVal) ? sireVal : null;
            mare.modData.Remove(HorseHelper.SireIVsKey);

            Game1.activeClickableMenu = new NamingMenu(
                name =>
                {
                    FarmAnimal foal = new FarmAnimal("Tycoon.Horse", Game1.Multiplayer.getNewID(), Game1.player.UniqueMultiplayerID);
                    foal.Name = name;
                    foal.displayName = name;
                    foal.parentId.Value = mare.myID.Value;
                    if (!TryApplyInheritedStats(foal, mare, sireIVsRaw))
                        foal.GetHorseStats().RandomizeStats(HorseStats.HorseSourceQuality.Starter);
                    interior.adoptAnimal(foal);
                    foal.Position = mare.Position + new Vector2(64f, 0f);

                    Logger.LogVerbose($"{mare.Name} gave birth to foal '{name}' ({foal.myID.Value}).");
                    Game1.exitActiveMenu();
                    DeliverFoals(dueMares, index + 1);
                },
                title: $"{mare.displayName} gave birth! Name the foal:"
            );
        }

        /// <summary>
        /// The foal inherits each IV from its parents (festival Stud Shop or barn stallion alike):
        /// the average of the mare's and sire's IV (rounded to the 10-step grid) shifted by one
        /// random step (-10/0/+10). Returns false when no sire data is recorded, so the caller
        /// falls back to random Starter stats.
        /// </summary>
        private static bool TryApplyInheritedStats(FarmAnimal foal, FarmAnimal mare, string? sireIVsRaw)
        {
            if (sireIVsRaw == null)
                return false;

            string[] parts = sireIVsRaw.Split(',');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out int sireSpeed)
                || !int.TryParse(parts[1], out int sireSprint)
                || !int.TryParse(parts[2], out int sireJump))
            {
                Logger.LogVerbose($"Ignoring malformed sire IV data '{sireIVsRaw}' on {mare.Name}.");
                return false;
            }

            Random rand = Game1.random;
            HorseStats mareStats = mare.GetHorseStats();
            HorseStats foalStats = foal.GetHorseStats();
            foalStats.SpeedIV = InheritIV(mareStats.SpeedIV, sireSpeed, rand);
            foalStats.SprintIV = InheritIV(mareStats.SprintIV, sireSprint, rand);
            foalStats.JumpIV = InheritIV(mareStats.JumpIV, sireJump, rand);
            foalStats.SpeedEV = 0;
            foalStats.SprintEV = 0;
            foalStats.JumpEV = 0;

            Logger.LogVerbose($"Foal inherited IVs {foalStats.SpeedIV}/{foalStats.SprintIV}/{foalStats.JumpIV} " +
                $"(mare {mareStats.SpeedIV}/{mareStats.SprintIV}/{mareStats.JumpIV}, sire {sireSpeed}/{sireSprint}/{sireJump}).");
            return true;
        }

        private static int InheritIV(int mareIV, int sireIV, Random rand)
        {
            int average = (int)Math.Round((mareIV + sireIV) / 2.0 / 10.0) * 10;
            int mutation = (rand.Next(3) - 1) * 10; // -10, 0, or +10
            return Math.Clamp(average + mutation, 0, HorseStats.IV_MAX);
        }
    }
}
