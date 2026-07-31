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
    public sealed partial class FestivalDefinition
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
        // Rider whose horse (see NpcHorseNames) is the one giving the pony ride. That horse is then
        // shown here instead of in the pasture with the other racers' horses. Null = a generic horse.
        public string? PenHorseRider;

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
        // Where a DQ'd player (and their horse) is teleported: just past the finish in the spectator area.
        public Point DqArrivalTile;

        // --- Ceremony layout (tiles) ---
        // Winner's circle tiles (1st, 2nd, 3rd place left-to-right).
        public Point[] WinnersCircleTiles = null!;
        // Where the race starter stands in the TMX Set-Up layer (pre-race). Player warps 1 tile south of
        // this on restart. Must match the character tile placed for them on that layer.
        public Point StarterStartTile;
        public Point StarterAnnouncerTile;
        // Tiles for racers who didn't make the podium, spread south of the winners circle.
        public Point[] SpectatorTiles = null!;

        // --- Race starter / announcer ---
        // The NPC the player walks up to in order to start the race. They also give the pre-race
        // announcement, the disqualification line and the awards commentary. Mayor Lewis hosts the
        // valley's own races; an away festival has its own host (Sandy runs the desert race), so this
        // name must match a character tile on the map's Set-Up layer.
        public string StarterName = "Lewis";
        // Display name used in the starter's closing ceremony line.
        public string FestivalDisplayName = "Horse Festival";
        // Small talk the starter makes the first time the player approaches them, before the "ready?"
        // question. Pages are separated with "#$b#" (click-through inside one conversation); trailing
        // "$0".."$3" picks the portrait for that page. Null = go straight to the question.
        public string? StarterGreeting;
        // The question that starts the race (host) and the line non-hosts get instead.
        public string StarterReadyQuestion = "Ready to start the race?";
        // Asked instead when the player turned up without a horse. The host's version starts the race
        // as soon as they accept, so its wording says so.
        public string StarterNoHorseQuestionHost =
            "It looks like you don't have a horse! Marnie has some available to borrow. Ready to ride one and start the race?";
        public string StarterNoHorseQuestion =
            "It looks like you don't have a horse! Marnie has some available to borrow for the race. Would you like to ride one?";
        public string StarterWaitingForHostLine = "We're just waiting on the host to start the race!";
        // Pre-race announcement, shown once every rider is in the stalls. Same "#$b#" page format.
        public string RaceAnnouncement =
            "What a beautiful day for a race! The weather is perfect, and the crowd is buzzing with excitement."
            + "#$b#"
            + "The horses look fit and ready, raring to run."
            + "#$b#"
            + "Let the race begin!$h";
        // Shown when the player rides off the course. Leading "$a" keeps the starter's angry portrait.
        public string StarterDqLine =
            "$a You've gone off the track! I'm afraid you are disqualified from this race.";
        // Awards ceremony commentary. "{0}" is the racer's name where one is announced.
        public string CeremonyOpeningLine = "What a spectacular race! Let's see how our riders placed!";
        public string CeremonyThirdPlaceLine = "In 3rd place... {0}! Congratulations!";
        public string CeremonySecondPlaceLine = "In 2nd place... {0}! Well done!";
        public string CeremonyFirstPlaceLine =
            "And the winner is... {0}! What a ride! You've earned the champion's trophy and a prize ticket!";
        public string CeremonyClosingLine = "Thank you all for participating in the {0}! See you next year!";

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

        // --- Going (ground) ---
        // Flat speed change applied on grass (fast) and mud (heavy) tiles during the race, in
        // getMovementSpeed units (~1 tile/sec per point, same scale as HorseStats.SprintSpeedBonus).
        // The tiles themselves are read straight off the map art (see FestivalRaceManager.Going.cs).
        public float GoingFastBonus = 1f;
        public float GoingHeavyBonus = -1f;

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
        // Tile the bookie (vanilla Bouncer) stands on; null = no bookie (walk-in festivals use
        // Pam's flat winner-takes-double book instead). The bookie posts per-racer fractional
        // odds computed from horse stats and pays out at those odds (see
        // FestivalRaceManager.Bookie.cs).
        public Point? BookieTile;
        public int BookieFacing = 2;
        // Top-left tile of the vanilla desert merchant's caravan (the trader who trades for Omni
        // Geodes in Calico Desert); null = no caravan. Drawn from the same LooseSprites sheet the
        // Desert uses, occupies a 14x5 collision footprint, and the two counter tiles at the front
        // open the vanilla "DesertTrade" shop (see FestivalRaceManager.Trader.cs).
        public Point? DesertTraderTile;

        // --- Tack stall display (opt-in) ---
        // Data/Shops key whose stock the stall's decorative saddles and mannequins mirror. The
        // display is READ BACK OUT of the shop rather than authored on the map, so the yearly
        // rotation in the CP pack's data/festival.json (the HorseTycoon_YEAR_MOD conditions) stays
        // the single source of truth and the stall always advertises what's really for sale that
        // year. Null = this map has no tack display (see FestivalRaceManager.TackDisplay.cs).
        public string? TackDisplayShopId;
        // Both slot lists are normally left EMPTY: the display slots are found by scanning the map
        // for the tilesheet's saddle and mannequin cells, so the stall can be moved, resized or
        // rearranged entirely in Tiled. Fill these in only to override that for a layout the scan
        // can't express (e.g. pinning a specific saddle to a specific tile).
        // Tiles on the "Front2" layer each holding one decorative saddle sprite, left to right.
        public Point[] TackDisplaySaddleTiles = Array.Empty<Point>();
        // Top-left tile of each 2x2 mannequin: top half on "Front" at y, bottom half on
        // "Buildings" at y+1. Mannequins get the priciest tack; the saddle tiles take the rest.
        public Point[] TackDisplayMannequinTiles = Array.Empty<Point>();

        // --- Bus arrival cinematic (opt-in) ---
        // When true, the festival opens with the vanilla-style bus driving in from the right before the
        // pasture phase. Park/drop tiles mirror the vanilla Desert bus (rest 17,24; player drops at 18,27).
        public bool BusArrival;
        public Point BusParkTile = new Point(21, 6);
        public Point BusDropTile = new Point(22, 10);
        // Tiles that send the player home when they walk onto them (the bus doorway). The bus stays parked
        // for the whole festival, like the Desert's, and stepping into its door is how you leave.
        // Null = the drop tile plus the tile directly above it (i.e. the doorway itself).
        public Point[]? BusExitTiles;
        // Where the bus drops everyone off on the way home, overriding the vanilla festival exit (the farm).
        // Defaults to the real Bus Stop's bus door, the same spot the Desert bus returns you to.
        public string BusReturnLocation = "BusStop";
        public Point BusReturnTile = new Point(22, 10);

        // --- Optional start-of-festival heads-up (opt-in) ---
        // For "away" festivals that are NOT registered in Data/Festivals/FestivalDates (so they don't close
        // the town), vanilla's town-wide "The X Festival is starting at Y" noon message never fires. Set this
        // to show our own global message at StartTime instead. Null = no heads-up (walk-in festivals rely on
        // the vanilla one). Shown only to players who can actually attend (bus repaired + trailer built).
        public string? HeadsUpMessage;

        // ====================================================================================
        // Registered festivals
        // ====================================================================================

        // --- Advance-notice letter (opt-in) ---
        // Letter delivered to every player's mailbox a few days before the festival, every year.
        // Null id = no letter. Text uses the usual Data/Mail codes (@ = farmer name, ^ = line break);
        // the title is appended as "[#]<title>" when the entry is injected in ModEntry's Data/Mail edit.
        public string? AnnouncementMailId;
        public int AnnouncementDaysBefore = 3;
        public string AnnouncementLetterText = "";
        public string AnnouncementLetterTitle = "";
        // When true the letter only reaches players who could actually attend an away race
        // (bus repaired + horse trailer built), matching the HeadsUpMessage rule.
        public bool AnnouncementRequiresBusAccess;

        /// <summary>The original Spring 19 Horse Festival in Cindersap Forest.</summary>
        public static FestivalDefinition Forest() => new()
        {
            EventId = "festival_spring19",
            Season = "spring",
            Day = 19,
            LocationName = "Forest",
            MapAssetKey = "CP.HorseTycoon_ForestFestival",

            AnnouncementMailId = "HorseTycoon.SpringFestivalNotice",
            AnnouncementLetterTitle = "The Spring Horse Festival",
            AnnouncementLetterText =
                "Dear @,^^In three days' time the valley gathers in Cindersap Forest for the Spring Horse Festival. "
                + "Come riding on your horse at noon and I'll see it entered in the race.^"
                + "^If you don't have one, Marnie is loaning them out. Pam will be taking wagers, as always.^"
                + "^   -Mayor Lewis",

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
            PenHorseRider = "Marnie",

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
            StarterStartTile = new Point(87, 18),
            StarterAnnouncerTile = new Point(56, 9),
            FestivalDisplayName = "Spring Horse Festival",
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

            // Leah's stall mirrors whatever her shop is stocking this year. The saddle slots and
            // mannequins are found by scanning the map, so move or add them freely in Tiled — no
            // tile coordinates needed here.
            TackDisplayShopId = "Festival_SpringHorseFestival_Leah",
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

            AnnouncementMailId = "HorseTycoon.FallFestivalNotice",
            AnnouncementLetterTitle = "The Fall Horse Festival",
            AnnouncementLetterText =
                "Dear @,^^Three days from now the Fall Horse Festival takes over the beach. We've marked out a course "
                + "along the sand and I'll warn you now, it's a jumping track with driftwood, crates, whatever the tide left us.^"
                + "^If your horse has been schooled over fences, this is the day to show it."
                + "^   -Mayor Lewis",

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
            FinishMin = new Point(125, 9),
            FinishMax = new Point(125, 16),
            DqZoneNorthOfY = -1,
            DqZoneEastOfX = -1,
            DqZoneWestOfX = 32,
            DqArrivalTile = new Point(44, 14),

            WinnersCircleTiles = new[]
            {
                new Point(58, 12), new Point(56, 12), new Point(54, 12),
            },
            StarterStartTile = new Point(24, 6),
            StarterAnnouncerTile = new Point(56, 9),
            FestivalDisplayName = "Fall Horse Festival",
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
                // Index 0/2/3 (Marnie/Abigail/Sebastian) are still placeholder straight-line routes and
                // must be re-recorded on the expanded map with ht_record_jumps.
                new[] { new Point(61, 13), new Point(128, 12) },
                // Index 1 = Leah's line, reduced to only the jump takeoff/landing tiles (chained
                // duplicates deduped) so A* walks straight between jumps instead of stalling on the
                // dense every-3s waypoints. Final (120, 10) is a non-jump anchor past the finish band
                // (X=125) so she crosses the line and registers a finish.
                new[]
                {
                    new Point(47, 21), new Point(50, 21), new Point(53, 16), new Point(53, 14), new Point(55, 14), new Point(57, 14),
                    new Point(68, 12), new Point(68, 10), new Point(70, 10), new Point(72, 10), new Point(74, 10), new Point(75, 10),
                    new Point(77, 10), new Point(79, 10), new Point(80, 10), new Point(82, 10), new Point(84, 10), new Point(85, 10),
                    new Point(88, 10), new Point(90, 17), new Point(90, 19), new Point(90, 21), new Point(87, 35), new Point(87, 37),
                    new Point(87, 39), new Point(89, 39), new Point(93, 39), new Point(95, 39), new Point(100, 40), new Point(103, 40),
                    new Point(106, 40), new Point(109, 40), new Point(112, 24), new Point(114, 24), new Point(115, 24), new Point(117, 24),
                    new Point(118, 24), new Point(120, 24), new Point(144, 35), new Point(146, 35), new Point(150, 36), new Point(153, 36),
                    new Point(157, 36), new Point(160, 36), new Point(167, 39), new Point(168, 39), new Point(170, 39), new Point(172, 39),
                    new Point(185, 34), new Point(185, 32), new Point(185, 30), new Point(187, 16), new Point(187, 13), new Point(182, 8),
                    new Point(180, 8), new Point(178, 8), new Point(177, 8), new Point(175, 8), new Point(173, 8), new Point(172, 8),
                    new Point(170, 8), new Point(168, 8), new Point(166, 8), new Point(164, 8), new Point(160, 10), new Point(158, 10),
                    new Point(157, 10), new Point(155, 10), new Point(148, 10), new Point(146, 10), new Point(145, 10), new Point(143, 10),
                    new Point(120, 10),
                },
                new[] { new Point(58, 17), new Point(128, 12) },
                new[] { new Point(56, 20), new Point(128, 12) },
            },

            // Jump zones authored in code, not the TMX. The map's NpcJumpApproach/NpcJumpLanding loader
            // pairs tiles by scan order (top→bottom, left→right), which can't express this curving track's
            // vertical and westward chained jumps; the pairs would cross-match. MinSkill 45 = Leah's jump
            // skill, so she clears every zone on her recorded line. Recorded via ht_record_jumps.
            NpcJumpZones = JumpZones(45,
                (47, 21, 50, 21), (53, 16, 53, 14), (53, 14, 55, 14), (55, 14, 57, 14),
                (68, 12, 68, 10), (68, 10, 70, 10), (70, 10, 72, 10), (72, 10, 74, 10),
                (75, 10, 77, 10), (77, 10, 79, 10), (80, 10, 82, 10), (82, 10, 84, 10),
                (85, 10, 88, 10), (90, 17, 90, 19), (90, 19, 90, 21), (87, 35, 87, 37),
                (87, 39, 89, 39), (93, 39, 95, 39), (100, 40, 103, 40), (106, 40, 109, 40),
                (112, 24, 114, 24), (115, 24, 117, 24), (118, 24, 120, 24), (144, 35, 146, 35),
                (150, 36, 153, 36), (157, 36, 160, 36), (167, 39, 168, 39), (168, 39, 170, 39),
                (170, 39, 172, 39), (185, 34, 185, 32), (185, 32, 185, 30), (187, 16, 187, 13),
                (182, 8, 180, 8), (180, 8, 178, 8), (177, 8, 175, 8), (175, 8, 173, 8),
                (172, 8, 170, 8), (170, 8, 168, 8), (168, 8, 166, 8), (166, 8, 164, 8),
                (160, 10, 158, 10), (157, 10, 155, 10), (148, 10, 146, 10), (145, 10, 143, 10)),

            FirstPlacePrizes = new[] { "(O)PrizeTicket", "(F)CP.HorseTycoon.HorseStatue" },
            SecondPlacePrizes = new[] { "(O)PrizeTicket" },
            ThirdPlacePrizes = new[] { "(O)PrizeTicket" },

            // Leah's beach stall, same deal as the spring one: slots are found by scanning the map.
            TackDisplayShopId = "Festival_FallHorseFestival_Leah",
        };

        /// <summary>
        /// TESTING ONLY: same Spring 19 Forest festival as <see cref="Forest"/>, but also triggerable on
        /// Spring 9 so it can be reached quickly without fast-forwarding. Remove when done testing.
        /// </summary>
        public static FestivalDefinition ForestSpringTest()
        {
            FestivalDefinition def = Forest();
            def.EventId = "festival_spring9";
            def.Day = 9;
            // The real Spring 19 festival already sends the advance notice; a second copy of the same
            // letter (and a negative notice day) makes no sense for the test slot.
            def.AnnouncementMailId = null;
            return def;
        }

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
            // The map is SVE's Desert, so the vanilla Desert bus tiles line up with the art (and the
            // map's own collision under the bus). Matches summer19.json's farmer start tile, 18 27.
            BusParkTile = new Point(17, 24),
            BusDropTile = new Point(18, 27),
            HeadsUpMessage = "The bus to the Summer Horse Festival is now boarding.",

            AnnouncementMailId = "HorseTycoon.SummerFestivalNotice",
            AnnouncementLetterTitle = "Invitation: Summer Horse Race",
            AnnouncementLetterText =
                "Dear @,^^Our scouts have had their eye on your stable, and the Committee is pleased to invite you to "
                + "compete at this year's Summer Horse Race, held three days from now, out of town.^"
                + "^The bus leaves at 12:00pm and a horse trailer is needed to carry "
                + "your horses. Come prepared, there will be traders the purse is worth the trip.^"
                + "^   -The Horse Racing Committee",
            AnnouncementRequiresBusAccess = true,

            // STALE: layout below was authored for the old Summer-HorseRace.tmx (65x85) and still needs
            // re-tuning for Desert-HorseRace.tmx (SVE's Desert, 60x156) with ht_race_tile. Only the bus
            // tiles and StartStall have been moved so far.
            // Old layout: a festival plaza (rows 30-49)
            // holding the market shops + for-sale paddock and the horse pens, above a large
            // fenced oval track (rows 50-82). The race runs along the top "home straight"
            // (lane rows 51-59) west->east. Fine-tune tiles in-game with ht_race_tile.
            // Grazing spots inside the contestants' pasture, west of the road.
            PenSlots = new[]
            {
                new Point(1, 40), new Point(4, 41), new Point(1, 44), new Point(4, 44),
                new Point(2, 46), new Point(3, 48), new Point(3, 50), new Point(1, 52),
            },
            // Decorative "horses for sale" shown in the fenced paddock beside the shops.
            PastureBgSlots = new[]
            {
                new Point(24, 48), new Point(28, 47), new Point(31, 49),
                new Point(30, 50), new Point(27, 51), new Point(23, 51),
            },
            PenHorseTile = null,

            // Starting stalls at the west end of the home straight; horses break east.
            // Centered so a full 8-horse field puts the topmost stall at (9, 65): rows 65..79 (odd slots
            // below center, even above), with the enclosing fence spanning y 64..80 and x 8..10.
            StartStall = new Point(9, 71),
            StallFenceId = "323", // stone, to suit the desert venue
            // Finish band: vertical line at x=9, spanning y 81..91 (just south of the starting stalls).
            FinishMin = new Point(9, 81),
            FinishMax = new Point(9, 91),
            DqZoneNorthOfY = -1,
            DqZoneEastOfX = -1,
            DqZoneWestOfX = -1,
            // Run-off area west of the finish line, inside the finish chamber.
            DqArrivalTile = new Point(5, 86),

            WinnersCircleTiles = new[]
            {
                new Point(30, 54), new Point(32, 54), new Point(34, 54),
            },
            // Sandy hosts the desert race: she's the one you walk up to to start it, she calls the
            // riders to the gate and she runs the awards. Her tile matches the Sandy character tile on
            // the map's Set-Up layer. Lewis is here too, but only as a guest (see data/summer19.json).
            StarterName = "Sandy",
            StarterStartTile = new Point(20, 60),
            StarterAnnouncerTile = new Point(28, 54),
            FestivalDisplayName = "Summer Horse Race",

            StarterGreeting =
                "Well, look who found their way out to the desert! I was hoping you'd come.$1"
                + "#$b#"
                + "Folks think there's nothing out here but sand and cactus. One day a year I get to prove them wrong.$0"
                + "#$b#"
                + "The traders came in on the early to set up and the race track sand has been raked smooth.$1",
            StarterReadyQuestion = "So, are you ready to start the race?",
            StarterWaitingForHostLine = "Hold your horses, sugar! We're still waiting on the rest of your party.",
            StarterNoHorseQuestionHost =
                "Sugar, you can't run a race on foot! There are a few spares in the paddock. Want to take one out and get us started?",
            StarterNoHorseQuestion =
                "Sugar, you can't run a race on foot! There are a few spares in the paddock. Want to take one out?",

            RaceAnnouncement =
                "Welcome, everyone, to the Summer Horse Race! I am so glad you all made the trip out."
                + "#$b#"
                + "It's a hot one today, and this course is a long one. Sand, switchbacks, and not a lick of shade."
                + "#$b#"
                + "Riders, take up your reins and let them run!$h",
            StarterDqLine =
                "$a Sugar, you're way off the course! I'm afraid that's a disqualification. Get yourself some water.",
            CeremonyOpeningLine = "What a race! I don't think this old desert has ever seen anything like it!",
            CeremonyThirdPlaceLine = "Third place goes to... {0}! You rode a tight race out there.",
            CeremonySecondPlaceLine = "And in second... {0}! So close, sugar. So close!",
            CeremonyFirstPlaceLine =
                "Your winner, out here in the sand and the heat... {0}! Come get your Ember tack, champion. Nobody else in the valley will be wearing it!",
            CeremonyClosingLine =
                "Thank you all for coming out to the {0}!",
            SpectatorTiles = new[]
            {
                new Point(30, 57), new Point(32, 57), new Point(34, 57),
                new Point(28, 57), new Point(36, 57),
            },

            // Away-festival market stalls (summer festival only). Jadu's item stall sits on the way
            // down from the bus; the stud master and horse seller stand together above the track.
            HorseSellerTile = new Point(28, 58),
            HorseSellerFacing = 2,
            StudShopTile = new Point(26, 58),
            StudShopFacing = 2,
            ItemShopTile = new Point(9, 36),
            ItemShopFacing = 2,
            // The Bouncer runs the betting book at away races, at the east end of the shop row.
            BookieTile = new Point(44, 58),
            BookieFacing = 2,
            // The desert merchant's caravan parks north of the road, roughly where it stands in the
            // vanilla Desert. The tile is the top-left corner of the 14-wide wagon art.
            DesertTraderTile = new Point(33, 18),

            NpcRiderNames = new[] { "Marnie", "Leah", "Abigail", "Sebastian" },
            NpcRiderSpeeds = new[] { 5, 10, 15, 20 },
            NpcRiderSprints = new[] { 20, 35, 45, 45 },
            NpcRiderJumps = new[] { 20, 45, 65, 80 },
            NpcJumpMinSkill = 50,
            // The desert course is a serpentine, not an oval: east along the top straight, down the
            // x42-57 corridor, back north up x59-67, east into the x69-77 corridor and all the way
            // south to the bottom straight, then north again through the four switchback rooms
            // (rows 126-135, 120-124, 113-118, 105-111) into the finish chamber (rows 81-91), where
            // the line at x=9 is crossed heading west. Each route is the same course on its own lane,
            // so the four fields run abreast; all four are ~500 tiles long, within 3% of each other.
            // Every leg was checked against the map's Buildings collision, so A* never has to
            // improvise between waypoints. Ends past the line at x=4 so the crossing registers.
            NpcRaceRoutes = new[]
            {
                new[]
                {
                    new Point(20, 74), new Point(48, 74),                        // top straight, east
                    new Point(53, 78), new Point(53, 94),                        // down the x42-57 corridor
                    new Point(61, 99), new Point(61, 84), new Point(61, 76),     // east, then back north up x59-67
                    new Point(73, 76), new Point(73, 90), new Point(73, 120), new Point(73, 137), // south down the east corridor
                    new Point(5, 137), new Point(5, 130),                        // bottom straight west, up through the row-136 gap
                    new Point(30, 132), new Point(63, 132), new Point(63, 121),  // east, up through the row-125 gap
                    new Point(24, 121), new Point(24, 114),                      // west, up through the row-119 gap
                    new Point(61, 114), new Point(61, 106),                      // east, up through the row-112 gap
                    new Point(32, 106), new Point(32, 96), new Point(32, 86),    // west, up through the row-104 and row-92 gaps
                    new Point(4, 86),                                            // west across the finish line
                },
                new[]
                {
                    new Point(20, 75), new Point(48, 75),
                    new Point(54, 78), new Point(54, 94),
                    new Point(63, 99), new Point(63, 84), new Point(63, 76),
                    new Point(74, 76), new Point(74, 90), new Point(74, 120), new Point(74, 138),
                    new Point(6, 138), new Point(6, 130),
                    new Point(30, 133), new Point(64, 133), new Point(64, 122),
                    new Point(25, 122), new Point(25, 115),
                    new Point(62, 115), new Point(62, 107),
                    new Point(34, 107), new Point(34, 96), new Point(34, 87),
                    new Point(4, 87),
                },
                new[]
                {
                    new Point(20, 76), new Point(48, 76),
                    new Point(55, 78), new Point(55, 94),
                    new Point(65, 99), new Point(65, 84), new Point(65, 76),
                    new Point(75, 76), new Point(75, 90), new Point(75, 120), new Point(75, 139),
                    new Point(7, 139), new Point(7, 130),
                    new Point(30, 134), new Point(65, 134), new Point(65, 123),
                    new Point(26, 123), new Point(26, 116),
                    new Point(63, 116), new Point(63, 108),
                    new Point(36, 108), new Point(36, 96), new Point(36, 88),
                    new Point(4, 88),
                },
                new[]
                {
                    new Point(20, 77), new Point(48, 77),
                    new Point(57, 78), new Point(57, 94),
                    new Point(67, 99), new Point(67, 84), new Point(67, 76),
                    new Point(76, 76), new Point(76, 90), new Point(76, 120), new Point(76, 140),
                    new Point(8, 140), new Point(8, 130),
                    new Point(30, 135), new Point(66, 135), new Point(66, 124),
                    new Point(27, 124), new Point(27, 117),
                    new Point(64, 117), new Point(64, 109),
                    new Point(38, 109), new Point(38, 96), new Point(38, 89),
                    new Point(4, 89),
                },
            },

            // Ember tack instead of the trophy the other two festivals hand out: it's sold nowhere
            // (see the gradient rotation in the CP pack's data/items.json and data/festival.json),
            // so winning here is the only way to get it.
            FirstPlacePrizes = new[] { "(O)PrizeTicket", "(O)HorseTycoon.SaddleEmber" },
            SecondPlacePrizes = new[] { "(O)PrizeTicket" },
            ThirdPlacePrizes = new[] { "(O)PrizeTicket" },

            // The venue is Calico Desert, so wear its theme for the set-up and the ceremony ("wavy" is
            // the Desert location context's DefaultMusic). The race switches to the Outlaw showdown
            // track from Journey of the Prairie King.
            PastureMusic = "wavy",
            RaceMusic = "cowboy_outlawsong",
        };
    }

    /// <summary>
    /// One jump obstacle on the race course. NPCs whose TotalJump meets MinSkill clear
    /// it cleanly (arc to LandingTile); those below MinSkill do an in-place blocked hop.
    /// </summary>
    public sealed partial class FestivalDefinition
    {
        /// <summary>
        /// Builds an <see cref="NpcJumpZones"/> dictionary from explicit (approach → landing) tile pairs,
        /// all sharing one MinSkill. Explicit pairing sidesteps the TMX scan-order pairing, which can't
        /// represent curving tracks with vertical or westward chained jumps.
        /// </summary>
        private static System.Collections.Generic.Dictionary<Point, NpcJumpZone> JumpZones(
            int minSkill, params (int ax, int ay, int lx, int ly)[] pairs)
        {
            var zones = new System.Collections.Generic.Dictionary<Point, NpcJumpZone>();
            foreach (var (ax, ay, lx, ly) in pairs)
                zones[new Point(ax, ay)] = new NpcJumpZone { LandingTile = new Point(lx, ly), MinSkill = minSkill };
            return zones;
        }
    }

    public sealed class NpcJumpZone
    {
        /// <summary>Tile the NPC lands on after a successful jump.</summary>
        public Point LandingTile;
        /// <summary>Minimum TotalJump skill required to clear this obstacle.</summary>
        public int MinSkill;
    }
}
