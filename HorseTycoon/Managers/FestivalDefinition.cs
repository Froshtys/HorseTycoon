using Microsoft.Xna.Framework;

namespace HorseTycoon
{
    /// <summary>
    /// All per-festival data the <see cref="FestivalRaceManager"/> needs to run one horse-racing
    /// festival: when/where it happens, which temporary map it loads, the tile layout of the course
    /// and ceremony, the NPC racer roster and their hand-authored routes, and the prizes/economy.
    /// Behavior (race logic, betting flow, ceremony state machine, multiplayer sync) is shared across
    /// all festivals and lives in the manager; only these values vary per festival.
    ///
    /// Register one instance per festival in <c>FestivalRaceManager.Festivals</c>. Tile coordinates
    /// are tuned in-game with the <c>ht_race_tile</c> console command.
    /// </summary>
    public sealed class FestivalDefinition
    {
        // --- Identity / scheduling ---
        // Event id format: "festival_" + the file key from Event.tryToLoadFestival (e.g. Data/Festivals/spring21).
        public string EventId = null!;
        public string Season = null!;        // "spring", "fall", ...
        public int Day;
        public int StartTime = 1200;
        public int EndTime = 1800;
        public string LocationName = null!;  // map/location the festival event runs in, e.g. "Forest"
        // Temp map asset key passed to changeToTemporaryMap in the festival's set-up script.
        public string MapAssetKey = null!;

        // --- Pasture phase layout (tiles) ---
        // Pen slots for player horses + NPC racer horses shown during the pasture phase.
        // Slots fill in order: players first (by UniqueMultiplayerID), then NPC racers.
        public Point[] PenSlots = null!;
        // Decorative generated horses displayed in the background pasture during the festival.
        public Point[] PastureBgSlots = null!;
        // Decorative pony-ride horse placed in the pen (e.g. left of Leah's house in the Forest map).
        // Null means no pony ride (e.g. Fall beach race has no Jas pony ride).
        public Point? PenHorseTile;

        // --- Race course layout (tiles) ---
        // Stall i's horse tile is (StartStall.X, StartStall.Y + offset); horses break east into the course.
        public Point StartStall;
        // Item ID for the fence panels that form the starting stalls ("322"=wood, "298"=hardwood, etc.).
        public string StallFenceId = "322";
        // Finish band (inclusive tile rectangle).
        public Point FinishMin;
        public Point FinishMax;
        // Disqualification zone: north of the starting-gate's north fence AND east of the finish-line's
        // east barrier. A racer entering this zone has jumped off the track and is disqualified.
        public int DqZoneNorthOfY;   // player.Tile.Y < this value
        public int DqZoneEastOfX;    // player.Tile.X > this value; -1 = disabled
        public int DqZoneWestOfX = -1; // player.Tile.X < this value; -1 = disabled
        // Where a DQ'd player (and their horse) is teleported — just past the finish in the spectator area.
        public Point DqArrivalTile;

        // --- Ceremony layout (tiles) ---
        // Winner's circle tiles (1st, 2nd, 3rd place left-to-right).
        public Point[] WinnersCircleTiles = null!;
        // Where Lewis stands in the TMX Set-Up layer (pre-race). Player warps 1 tile south of this on restart.
        public Point LewisStartTile;
        public Point LewisAnnouncerTile;
        // Tiles for racers who didn't make the podium, spread south of the winners circle.
        public Point[] SpectatorTiles = null!;

        // --- NPC racers + routes ---
        // NPC racers ride in the race as AI opponents. Speed stat drives tiles/sec via 5 + (speed / 20).
        public string[] NpcRiderNames = null!;
        public int[] NpcRiderSpeeds = null!;
        public int[] NpcRiderSprints = null!;
        // Jump skill (0–100) per NPC racer. Drives route selection and arc height.
        public int[] NpcRiderJumps = null!;
        // Minimum TotalJump required to use NpcJumpRoutes instead of NpcRaceRoutes.
        public int NpcJumpMinSkill = 50;
        // Per-NPC race routes. NPCs are assigned a route by index (cycling if there are more NPCs than routes).
        // Each route is a sequence of tile waypoints ending past the finish line.
        public Point[][] NpcRaceRoutes = null!;
        // Alternate routes for NPCs with TotalJump >= NpcJumpMinSkill; includes approach/landing tiles
        // that thread through jump obstacles instead of detouring around them.
        // Indexed same as NpcRaceRoutes. Leave null to fall back to NpcRaceRoutes for all NPCs.
        public Point[][]? NpcJumpRoutes = null;
        // Maps each jump-obstacle approach tile → jump zone data. When an NPC on a jump route reaches
        // an approach tile, skill is checked: if TotalJump >= MinSkill the NPC clears the obstacle
        // (arc to LandingTile); otherwise they do an in-place blocked hop and lose time.
        public System.Collections.Generic.Dictionary<Point, NpcJumpZone> NpcJumpZones = new();

        // --- Economy / rewards ---
        // Offered bet amounts. Any amount >= 1000 is only offered from year 2 onward (matches Pam's book).
        public int[] BetAmounts = { 250, 500, 1000 };
        // Qualified item ids awarded at the ceremony for each placement.
        public string[] FirstPlacePrizes = null!;
        public string[] SecondPlacePrizes = null!;
        public string[] ThirdPlacePrizes = null!;

        // --- Cosmetic ---
        public string PastureMusic = "CloudCountry";
        public string RaceMusic = "Cowboy_OVERWORLD";

        // --- Festival shop NPCs (opt-in) ---
        // All three stall keepers are SVE characters. The sprite name doubles as the actor's
        // display name and resolves to "Characters/<Name>": SVE loads those sheets itself, and
        // without SVE the CP pack loads bundled copies (data/festivalnpcs.json), so the sheets
        // always exist.
        // Tile the Horse Trader (Alesia) stands on during the pasture phase; null = no horse
        // seller. Talking to her opens the daily sale list (Special horses at
        // HorseMarket.SaleGoldPerIvPoint).
        public Point? HorseSellerTile;
        public int HorseSellerFacing = 2;
        public string HorseSellerSprite = "Alesia";
        // Tile the Stud Master (Isaac) stands on; null = no stud shop. Talking to him offers stud
        // services: pay the fee, then pick one of the horses you brought to breed.
        public Point? StudShopTile;
        public int StudShopFacing = 2;
        public string StudShopSprite = "Isaac";
        // Tile the item stall keeper (Jadu) stands on; null = no item shop. Talking to them opens
        // the Data/Shops stock defined in the CP pack (Gold Carrot Seeds + one random IV potion,
        // see data/ivpotions.json).
        public Point? ItemShopTile;
        public int ItemShopFacing = 2;
        public string ItemShopSprite = "Jadu";

        // --- Bus arrival cinematic (opt-in) ---
        // When true, the festival opens with the vanilla-style bus driving in from the right before the
        // pasture phase. Park/drop tiles mirror the vanilla Desert bus (rest 17,24; player drops at 18,27).
        public bool BusArrival;
        public Point BusParkTile = new Point(21, 6);
        public Point BusDropTile = new Point(22, 10);

        // --- Optional start-of-festival heads-up (opt-in) ---
        // For "away" festivals that are NOT registered in Data/Festivals/FestivalDates (so they don't close
        // the town), vanilla's town-wide "The X Festival is starting at Y" noon message never fires. Set this
        // to show our own global message at StartTime instead. Null = no heads-up (walk-in festivals rely on
        // the vanilla one). Shown only to players who can actually attend (bus repaired + trailer built).
        public string? HeadsUpMessage;

        // ====================================================================================
        // Registered festivals
        // ====================================================================================

        /// <summary>The original Spring 19 Horse Festival in Cindersap Forest.</summary>
        public static FestivalDefinition Forest() => new()
        {
            EventId = "festival_spring19",
            Season = "spring",
            Day = 19,
            LocationName = "Forest",
            MapAssetKey = "CP.HorseTycoon_ForestFestival",

            PenSlots = new[]
            {
                new Point(80, 32), new Point(73, 34), new Point(75, 32), new Point(69, 29),
                new Point(69, 32), new Point(71, 28), new Point(72, 30), new Point(75, 29),
            },
            PastureBgSlots = new[]
            {
                new Point(98, 20), new Point(94, 20), new Point(98, 16), new Point(102, 20),
            },
            PenHorseTile = new Point(94, 31),

            StartStall = new Point(39, 48),
            FinishMin = new Point(40, 11),
            FinishMax = new Point(40, 17),
            DqZoneNorthOfY = 43,
            DqZoneEastOfX = 41,
            DqArrivalTile = new Point(44, 14),

            WinnersCircleTiles = new[]
            {
                new Point(58, 12), // 1st place
                new Point(56, 12), // 2nd place
                new Point(54, 12), // 3rd place
            },
            LewisStartTile = new Point(87, 18),
            LewisAnnouncerTile = new Point(56, 9),
            SpectatorTiles = new[]
            {
                new Point(56, 15), // 4th: center
                new Point(54, 15), // 5th: one left
                new Point(58, 15), // 6th: one right
                new Point(52, 15), // 7th: two left
                new Point(60, 15), // 8th: two right
            },

            NpcRiderNames = new[] { "Marnie", "Leah", "Abigail", "Sebastian" },
            NpcRiderSpeeds = new[] { 15, 25, 35, 40 },
            NpcRiderSprints = new[] { 20, 35, 45, 45 },
            NpcRiderJumps = new[] { 20, 45, 65, 80 }, // Marnie low, Leah mid, Abigail/Sebastian high
            NpcJumpMinSkill = 50,
            // NpcJumpRoutes and NpcJumpZones populated after in-game route authoring (ht_race_tile).
            NpcRaceRoutes = new[]
            {
                // Route 0 (Marnie)
                new[]
                {
                    new Point(49, 49), new Point(58, 48), new Point(74, 49), new Point(85, 51),
                    new Point(88, 60), new Point(89, 66), new Point(91, 70), new Point(91, 75),
                    new Point(91, 80), new Point(87, 85), new Point(74, 82), new Point(73, 74),
                    new Point(72, 70), new Point(68, 70), new Point(59, 72), new Point(52, 74),
                    new Point(45, 77), new Point(38, 84), new Point(37, 89), new Point(38, 95),
                    new Point(31, 97), new Point(23, 94), new Point(20, 87), new Point(19, 84),
                    new Point(19, 78), new Point(20, 74), new Point(20, 66), new Point(19, 62),
                    new Point(20, 59), new Point(21, 54), new Point(24, 47), new Point(25, 41),
                    new Point(21, 37), new Point(18, 33), new Point(20, 22), new Point(23, 18),
                    new Point(27, 16), new Point(35, 16), new Point(43, 16), // past the finish
                },
                // Route 1 (Leah)
                new[]
                {
                    new Point(49, 45), new Point(58, 45), new Point(69, 45), new Point(76, 49),
                    new Point(87, 51), new Point(88, 61), new Point(91, 73), new Point(88, 81),
                    new Point(82, 84), new Point(76, 79), new Point(69, 70), new Point(57, 75),
                    new Point(45, 77), new Point(38, 84), new Point(38, 95), new Point(27, 93),
                    new Point(28, 88), new Point(24, 85), new Point(19, 79), new Point(18, 62),
                    new Point(22, 50), new Point(20, 41), new Point(18, 36), new Point(21, 20),
                    new Point(33, 14), new Point(44, 13), // past the finish
                },
                // Route 2 (Abigail)
                new[]
                {
                    new Point(54, 49), new Point(68, 49), new Point(75, 50), new Point(85, 50),
                    new Point(88, 63), new Point(92, 73), new Point(87, 80), new Point(86, 85),
                    new Point(73, 79), new Point(69, 72), new Point(60, 71), new Point(54, 77),
                    new Point(46, 79), new Point(41, 84), new Point(37, 89), new Point(40, 93),
                    new Point(39, 96), new Point(31, 97), new Point(22, 92), new Point(18, 83),
                    new Point(17, 75), new Point(17, 63), new Point(17, 55), new Point(17, 48),
                    new Point(16, 44), new Point(18, 39), new Point(18, 35), new Point(19, 25),
                    new Point(24, 17), new Point(33, 14), new Point(37, 14), new Point(46, 14), // past the finish
                },
                // Route 3 (Sebastian)
                new[]
                {
                    new Point(52, 47), new Point(58, 48), new Point(65, 48), new Point(72, 48),
                    new Point(84, 49), new Point(88, 58), new Point(89, 63), new Point(91, 68),
                    new Point(91, 72), new Point(90, 76), new Point(89, 81), new Point(85, 85),
                    new Point(79, 84), new Point(75, 82), new Point(72, 76), new Point(70, 72),
                    new Point(59, 70), new Point(49, 75), new Point(41, 78), new Point(38, 84),
                    new Point(38, 89), new Point(38, 96), new Point(32, 97), new Point(23, 93),
                    new Point(19, 85), new Point(18, 77), new Point(18, 68), new Point(17, 59),
                    new Point(22, 53), new Point(25, 46), new Point(21, 42), new Point(18, 35),
                    new Point(18, 25), new Point(22, 20), new Point(27, 15), new Point(37, 15),
                    new Point(47, 13), // past the finish
                },
            },

            FirstPlacePrizes = new[] { "(O)PrizeTicket", "(F)CP.HorseTycoon.HorseStatue" },
            SecondPlacePrizes = new[] { "(O)PrizeTicket" },
            ThirdPlacePrizes = new[] { "(O)PrizeTicket" },
        };

        /// <summary>
        /// The Fall 19 Horse Festival on the beach (CP.HorseTycoon_FallBeach).
        /// SCAFFOLD: identity/scheduling/map are final, but every tile coordinate and route below is a
        /// placeholder copied from the Forest layout and MUST be re-authored for the smaller beach map
        /// (~104x50) in-game with `ht_race_tile`. Prizes/dialog can also be tuned later.
        /// </summary>
        public static FestivalDefinition FallBeach() => new()
        {
            EventId = "festival_fall19",
            Season = "fall",
            Day = 19,
            LocationName = "Beach",
            MapAssetKey = "CP.HorseTycoon_FallBeach",

            // TODO(beach): re-author all tiles below for FallBeach.tmx — placeholders from Forest.
            PenSlots = new[]
            {
                new Point(7, 4), new Point(5, 5), new Point(6, 7), new Point(4, 9),
                new Point(6, 8), new Point(7, 11), new Point(5, 13), new Point(3, 12),
            },
            PastureBgSlots = Array.Empty<Point>(),
            PenHorseTile = null,

            StallFenceId = "298",

            // 8 total racers (4 players + 4 NPCs); topmost slot (slot 6, offset -6) lands the gate at (35, 6).
            StartStall = new Point(34, 14),
            FinishMin = new Point(91, 7),
            FinishMax = new Point(91, 14),
            DqZoneNorthOfY = -1,
            DqZoneEastOfX = -1,
            DqZoneWestOfX = 32,
            DqArrivalTile = new Point(44, 14),

            WinnersCircleTiles = new[]
            {
                new Point(58, 12), new Point(56, 12), new Point(54, 12),
            },
            LewisStartTile = new Point(24, 6),
            LewisAnnouncerTile = new Point(56, 9),
            SpectatorTiles = new[]
            {
                new Point(56, 15), // 4th: center
                new Point(54, 15), // 5th: one left
                new Point(58, 15), // 6th: one right
                new Point(52, 15), // 7th: two left
                new Point(60, 15), // 8th: two right
            },

            NpcRiderNames = new[] { "Marnie", "Leah", "Abigail", "Sebastian" },
            NpcRiderSpeeds = new[] { 5, 10, 15, 20 },
            NpcRiderSprints = new[] { 20, 35, 45, 45 },
            NpcRiderJumps = new[] { 20, 45, 65, 80 },
            NpcJumpMinSkill = 50,
            NpcRaceRoutes = new[]
            {
                new[] { new Point(61, 13), new Point(91, 12) },
                new[] { new Point(57, 10), new Point(91, 12) },
                new[] { new Point(58, 17), new Point(91, 12) },
                new[] { new Point(56, 20), new Point(91, 12) },
            },

            FirstPlacePrizes = new[] { "(O)PrizeTicket", "(F)CP.HorseTycoon.HorseStatue" },
            SecondPlacePrizes = new[] { "(O)PrizeTicket" },
            ThirdPlacePrizes = new[] { "(O)PrizeTicket" },
        };

        /// <summary>
        /// The Summer 19 Horse Festival, reached by buying a bus ticket at the real Bus Stop. Hosted in a
        /// dedicated CP custom location (Custom_HorseTycoon_SummerFestival) whose map is a copy of the
        /// vanilla Bus Stop (CP.HorseTycoon_SummerBusFestival, 65x30).
        /// SCAFFOLD: identity/scheduling/map/entry are final, but every tile coordinate and route below is a
        /// placeholder sized to fit the 65x30 map and MUST be re-authored in-game with `ht_race_tile`.
        /// </summary>
        public static FestivalDefinition SummerBusStop() => new()
        {
            EventId = "festival_summer19",
            Season = "summer",
            Day = 19,
            LocationName = "Custom_HorseTycoon_SummerFestival",
            MapAssetKey = "CP.HorseTycoon_SummerBusFestival",
            BusArrival = true,
            HeadsUpMessage = "The bus to the Summer Horse Festival is now boarding at the Bus Stop.",

            // Layout authored for Summer-HorseRace.tmx (65x85): a festival plaza (rows 30-49)
            // holding the market shops + for-sale paddock and the horse pens, above a large
            // fenced oval track (rows 50-82). The race runs along the top "home straight"
            // (lane rows 51-59) west->east. Fine-tune tiles in-game with ht_race_tile.
            PenSlots = new[]
            {
                new Point(8, 40), new Point(11, 40), new Point(14, 40), new Point(17, 40),
                new Point(8, 44), new Point(11, 44), new Point(14, 44), new Point(17, 44),
            },
            // Decorative "horses for sale" shown in the fenced paddock beside the shops.
            PastureBgSlots = new[]
            {
                new Point(47, 40), new Point(50, 40), new Point(53, 40),
                new Point(48, 44), new Point(51, 44), new Point(54, 44),
            },
            PenHorseTile = null,

            // Starting stalls at the west end of the home straight; horses break east.
            StartStall = new Point(6, 52),
            StallFenceId = "322",
            // Finish band at the east end of the home straight.
            FinishMin = new Point(56, 51),
            FinishMax = new Point(56, 59),
            DqZoneNorthOfY = -1,
            DqZoneEastOfX = -1,
            DqZoneWestOfX = -1,
            DqArrivalTile = new Point(50, 55),

            WinnersCircleTiles = new[]
            {
                new Point(30, 54), new Point(32, 54), new Point(34, 54),
            },
            LewisStartTile = new Point(32, 48),
            LewisAnnouncerTile = new Point(28, 54),
            SpectatorTiles = new[]
            {
                new Point(30, 57), new Point(32, 57), new Point(34, 57),
                new Point(28, 57), new Point(36, 57),
            },

            // Away-festival market stalls: horse seller + stud shop, in the plaza beside the
            // for-sale paddock (summer festival only).
            HorseSellerTile = new Point(40, 42),
            HorseSellerFacing = 2,
            StudShopTile = new Point(43, 42),
            StudShopFacing = 2,
            ItemShopTile = new Point(37, 42),
            ItemShopFacing = 2,

            NpcRiderNames = new[] { "Marnie", "Leah", "Abigail", "Sebastian" },
            NpcRiderSpeeds = new[] { 5, 10, 15, 20 },
            NpcRiderSprints = new[] { 20, 35, 45, 45 },
            NpcRiderJumps = new[] { 20, 45, 65, 80 },
            NpcJumpMinSkill = 50,
            NpcRaceRoutes = new[]
            {
                new[] { new Point(12, 52), new Point(56, 52) },
                new[] { new Point(12, 54), new Point(56, 54) },
                new[] { new Point(12, 56), new Point(56, 56) },
                new[] { new Point(12, 58), new Point(56, 58) },
            },

            FirstPlacePrizes = new[] { "(O)PrizeTicket", "(F)CP.HorseTycoon.HorseStatue" },
            SecondPlacePrizes = new[] { "(O)PrizeTicket" },
            ThirdPlacePrizes = new[] { "(O)PrizeTicket" },
        };
    }

    /// <summary>
    /// One jump obstacle on the race course. NPCs whose TotalJump meets MinSkill clear
    /// it cleanly (arc to LandingTile); those below MinSkill do an in-place blocked hop.
    /// </summary>
    public sealed class NpcJumpZone
    {
        /// <summary>Tile the NPC lands on after a successful jump.</summary>
        public Point LandingTile;
        /// <summary>Minimum TotalJump skill required to clear this obstacle.</summary>
        public int MinSkill;
    }
}
