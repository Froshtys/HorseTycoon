using HorseTycoon.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;

namespace HorseTycoon
{
    /// <summary>One horse offered by a festival shop NPC, either for sale or for stud services.</summary>
    public sealed class HorseOffer
    {
        public string Name = null!;
        /// <summary>Skin id from Data/FarmAnimals ("BlueRoan", "Bay", ...) or "" for the base Roan.</summary>
        public string SkinId = "";
        public int SpeedIV;
        public int SprintIV;
        public int JumpIV;
        public int Price;
        /// <summary>Sale offers: bought by someone, so it's off the market for everyone (synced).</summary>
        public bool Purchased;
        /// <summary>Stud offers: the local player already paid this stallion's fee today, so he's off
        /// their list. Deliberately not synced — other players can still hire him.</summary>
        public bool UsedByLocalPlayer;

        /// <summary>Whether the local player can still pick this offer in a shop menu.</summary>
        public bool IsAvailable => !Purchased && !UsedByLocalPlayer;

        /// <summary>Total IV segments (each segment = 10 stat points); drives pricing.</summary>
        public int IvPoints => (SpeedIV + SprintIV + JumpIV) / 10;
    }

    /// <summary>
    /// The festival horse market: generates the Horse Seller's sale list and the Stud Shop's stud
    /// list (deterministic per day so every client and every menu open sees the same horses), and
    /// performs the host-authoritative delivery of purchased horses / stud pregnancies. Farmhands
    /// send a mod message; the host creates the FarmAnimal or marks the mare pregnant.
    /// </summary>
    public static class HorseMarket
    {
        public const int SaleGoldPerIvPoint = 1000;
        public const int StudGoldPerIvPoint = 500;
        public const int SaleOfferCount = 6;
        public const int StudOfferCount = 4;

        private const string MsgBuyHorse = "MarketBuyHorse";
        private const string MsgStudService = "MarketStudService";
        private const string MsgOfferSold = "MarketOfferSold";
        private record BuyHorseMessage(string Name, string SkinId, int SpeedIV, int SprintIV, int JumpIV, long OwnerId);
        private record StudServiceMessage(long MareId, int SpeedIV, int SprintIV, int JumpIV);
        // Sale offers are generated deterministically on every client, so an index (plus the day, to
        // ignore stale messages across a day rollover) identifies the same offer everywhere.
        private record OfferSoldMessage(int Day, int Index);

        private static IModHelper _helper = null!;
        private static IMonitor _monitor = null!;

        // Offers are cached for the in-game day they were generated on.
        private static List<HorseOffer>? _saleOffers;
        private static List<HorseOffer>? _studOffers;
        private static int _offersDay = -1;

        // Skin ids from the Tycoon.Horse Data/FarmAnimals entry; "" = base Roan texture.
        private static readonly string[] SkinIds = Patches.HorseTexturePatches.AllSkinIds;

        private static readonly string[] NameFirstParts =
        {
            "Midnight", "Copper", "Silver", "Storm", "Honey", "Shadow", "Golden",
            "Winter", "Maple", "Thunder", "Clover", "Ember", "Willow", "Star",
            "River", "Sage", "Comet", "Indigo", "Rusty", "Velvet",
        };
        private static readonly string[] NameSecondParts =
        {
            "Dancer", "Runner", "Blaze", "Whisper", "Arrow", "Song", "Drift",
            "Gallop", "Spirit", "Flash", "Heart", "Wind", "Stride", "Mist",
        };

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;
            helper.Events.Multiplayer.ModMessageReceived += OnMessageReceived;
        }

        /// <summary>The Horse Seller's daily sale list (Special-quality horses).</summary>
        public static List<HorseOffer> GetSaleOffers()
        {
            EnsureOffers();
            return _saleOffers!;
        }

        /// <summary>The Stud Shop's daily stud list (Special-quality stallions).</summary>
        public static List<HorseOffer> GetStudOffers()
        {
            EnsureOffers();
            return _studOffers!;
        }

        private static void EnsureOffers()
        {
            int today = Game1.Date.TotalDays;
            if (_offersDay == today && _saleOffers != null && _studOffers != null)
                return;

            _offersDay = today;
            // Same seed base as the festival's pen-slot shuffle so all clients agree.
            var rng = new System.Random((int)(Game1.uniqueIDForThisGame ^ (uint)today) + 7);
            var usedNames = new HashSet<string>();
            _saleOffers = GenerateOffers(rng, SaleOfferCount, SaleGoldPerIvPoint, usedNames);
            _studOffers = GenerateOffers(rng, StudOfferCount, StudGoldPerIvPoint, usedNames);
            Logger.LogVerbose($"HorseMarket: generated {SaleOfferCount} sale + {StudOfferCount} stud offers for day {today}.");
        }

        private static List<HorseOffer> GenerateOffers(System.Random rng, int count, int goldPerIvPoint, HashSet<string> usedNames)
        {
            var offers = new List<HorseOffer>(count);
            for (int i = 0; i < count; i++)
            {
                // Special-quality IV rolls: 20/30/40 per stat (mirrors HorseStats.RandomizeStats).
                var offer = new HorseOffer
                {
                    Name = GenerateName(rng, usedNames),
                    SkinId = SkinIds[rng.Next(SkinIds.Length)],
                    SpeedIV = rng.Next(2, 5) * 10,
                    SprintIV = rng.Next(2, 5) * 10,
                    JumpIV = rng.Next(2, 5) * 10,
                };
                offer.Price = offer.IvPoints * goldPerIvPoint;
                offers.Add(offer);
            }
            return offers;
        }

        /// <summary>Longest generated name that fits the menus' name column without clipping
        /// into the stat bars. Char count (not pixel width) so all multiplayer clients agree.</summary>
        private const int MaxNameLength = 12;

        private static string GenerateName(System.Random rng, HashSet<string> usedNames)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                string name = $"{NameFirstParts[rng.Next(NameFirstParts.Length)]} {NameSecondParts[rng.Next(NameSecondParts.Length)]}";
                if (name.Length <= MaxNameLength && usedNames.Add(name))
                    return name;
            }
            return $"{NameFirstParts[rng.Next(NameFirstParts.Length)]} II";
        }

        /// <summary>
        /// Completes a horse purchase for the local player: deducts gold, marks the offer sold, and
        /// delivers the horse to a barn (directly on the host, via mod message from a farmhand).
        /// Money and barn availability must be checked by the caller first.
        /// </summary>
        public static void PurchaseHorse(HorseOffer offer)
        {
            Game1.player.Money -= offer.Price;
            offer.Purchased = true;
            Game1.playSound("purchase");
            Game1.dayTimeMoneyBox.moneyShakeTimer = 800;

            // Tell every other client this offer is off the market so their sale menus drop it too.
            int offerIndex = _saleOffers?.IndexOf(offer) ?? -1;
            if (offerIndex >= 0)
            {
                _helper.Multiplayer.SendMessage(
                    new OfferSoldMessage(_offersDay, offerIndex),
                    MsgOfferSold,
                    modIDs: new[] { _helper.ModRegistry.ModID });
            }

            if (IsHost)
            {
                DeliverPurchasedHorse(offer.Name, offer.SkinId, offer.SpeedIV, offer.SprintIV, offer.JumpIV, Game1.player.UniqueMultiplayerID);
            }
            else
            {
                _helper.Multiplayer.SendMessage(
                    new BuyHorseMessage(offer.Name, offer.SkinId, offer.SpeedIV, offer.SprintIV, offer.JumpIV, Game1.player.UniqueMultiplayerID),
                    MsgBuyHorse,
                    modIDs: new[] { _helper.ModRegistry.ModID });
            }
        }

        /// <summary>
        /// Fee the Stud Shop pays for one of the player's own stallions: 200g per 10 IV points
        /// plus 100g per 10 EV points, summed across Speed/Sprint/Jump.
        /// </summary>
        public static int GetStudOfferFee(FarmAnimal stud)
        {
            var stats = stud.GetHorseStats();
            int ivSum = stats.SpeedIV + stats.SprintIV + stats.JumpIV;
            int evSum = stats.SpeedEV + stats.SprintEV + stats.JumpEV;
            return ivSum / 10 * 200 + evSum / 10 * 100;
        }

        /// <summary>
        /// Completes a stud purchase for the local player: deducts gold and records the pregnancy on
        /// the mare (directly on the host, via mod message from a farmhand). The stud's IVs are stored
        /// on the mare so the foal inherits from both parents (see BreedingManager).
        /// </summary>
        public static void PurchaseStudService(HorseOffer stud, FarmAnimal mare)
        {
            Game1.player.Money -= stud.Price;
            // One service per stallion per player per festival; other players' lists are untouched.
            stud.UsedByLocalPlayer = true;
            Game1.playSound("purchase");
            Game1.dayTimeMoneyBox.moneyShakeTimer = 800;

            if (IsHost)
            {
                ApplyStudService(mare, stud.SpeedIV, stud.SprintIV, stud.JumpIV);
            }
            else
            {
                _helper.Multiplayer.SendMessage(
                    new StudServiceMessage(mare.myID.Value, stud.SpeedIV, stud.SprintIV, stud.JumpIV),
                    MsgStudService,
                    modIDs: new[] { _helper.ModRegistry.ModID });
            }
        }

        private static void OnMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != _helper.ModRegistry.ModID)
                return;

            // Every client (host and farmhands) marks sold offers so no one is shown a stale listing.
            if (e.Type == MsgOfferSold)
            {
                var soldMsg = e.ReadAs<OfferSoldMessage>();
                EnsureOffers();
                if (soldMsg.Day == _offersDay && soldMsg.Index >= 0 && soldMsg.Index < _saleOffers!.Count)
                {
                    _saleOffers[soldMsg.Index].Purchased = true;
                    Logger.LogVerbose($"HorseMarket: sale offer #{soldMsg.Index} ('{_saleOffers[soldMsg.Index].Name}') sold by another player.");
                }
                return;
            }

            // The remaining messages are delivery requests from farmhands, so host only.
            if (!IsHost)
                return;

            if (e.Type == MsgBuyHorse)
            {
                var msg = e.ReadAs<BuyHorseMessage>();
                DeliverPurchasedHorse(msg.Name, msg.SkinId, msg.SpeedIV, msg.SprintIV, msg.JumpIV, msg.OwnerId);
            }
            else if (e.Type == MsgStudService)
            {
                var msg = e.ReadAs<StudServiceMessage>();
                FarmAnimal? mare = HorseHelper.GetAllBarnHorses().FirstOrDefault(a => a.myID.Value == msg.MareId);
                if (mare != null)
                    ApplyStudService(mare, msg.SpeedIV, msg.SprintIV, msg.JumpIV);
                else
                    _monitor.Log($"Stud service message for unknown mare id {msg.MareId}.", LogLevel.Warn);
            }
        }

        /// <summary>Host-side: creates the purchased FarmAnimal in an available barn (mirrors
        /// HorseHelper.ConvertStableHorseToFarmAnimal's adult-horse setup).</summary>
        private static void DeliverPurchasedHorse(string name, string skinId, int speedIV, int sprintIV, int jumpIV, long ownerId)
        {
            Building? barn = HorseHelper.GetBarnWithHorseSpace();
            if (barn?.GetIndoors() is not AnimalHouse interior)
            {
                // The buyer checked space before paying; this only happens if the housing filled up
                // in the instant between their check and the host processing the delivery.
                _monitor.Log($"Cannot deliver purchased horse '{name}': no barn with horse space on the farm.", LogLevel.Warn);
                return;
            }

            FarmAnimal horse = new FarmAnimal("Tycoon.Horse", Game1.Multiplayer.getNewID(), ownerId);
            horse.Name = name;
            horse.displayName = name;
            if (!string.IsNullOrEmpty(skinId))
                horse.skinID.Value = skinId;
            horse.age.Value = System.Math.Max(28, (int)Game1.stats.DaysPlayed);

            var stats = horse.GetHorseStats();
            stats.SpeedIV = speedIV;
            stats.SprintIV = sprintIV;
            stats.JumpIV = jumpIV;
            stats.SpeedEV = 0;
            stats.SprintEV = 0;
            stats.JumpEV = 0;

            HorseHelper.RegisterHorseInBarn(horse, barn);
            // Reload after age/home/skin are set so the adult sprite and skin texture apply.
            horse.reload(barn);

            _monitor.Log($"Delivered purchased horse '{name}' (skin '{skinId}', IVs {speedIV}/{sprintIV}/{jumpIV}) to {barn.buildingType.Value}.", LogLevel.Info);
        }

        /// <summary>Host-side: stores the sire's IVs on the mare and starts the pregnancy.</summary>
        private static void ApplyStudService(FarmAnimal mare, int speedIV, int sprintIV, int jumpIV)
        {
            mare.modData[HorseHelper.SireIVsKey] = $"{speedIV},{sprintIV},{jumpIV}";
            BreedingManager.MakePregnant(mare);
        }

        private static bool IsHost =>
            !Game1.IsMultiplayer || Game1.serverHost == null || Game1.player.Equals(Game1.serverHost.Value);
    }
}
