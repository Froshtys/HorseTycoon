using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;

namespace HorseTycoon
{
    /// <summary>
    /// The away-festival betting book, run by the vanilla Bouncer. Unlike Pam's flat
    /// winner-takes-double book at the walk-in festivals, the Bouncer posts fractional odds per
    /// racer (5/1, 1/2, ...) computed from horse stats. NPC racers use their
    /// <see cref="FestivalDefinition"/> stat arrays, players use the best horse they brought. The book
    /// pays out stake + stake × odds on a win. Spawned during the pasture phase on festivals whose
    /// definition sets <see cref="FestivalDefinition.BookieTile"/>; clicks route here from
    /// <c>OnButtonPressed</c>. Payout is delivered through the same ceremony/quest flow as Pam's
    /// book (see DeliverBetResult).
    /// </summary>
    public partial class FestivalRaceManager
    {
        private const string BookieActorName = "HorseTycoonBookie";
        private const string BookieSpriteName = "Bouncer";

        // Odds the local player's bet was taken at. Denominator 0 means "no odds bet": either no
        // bet at all, or a Pam bet (flat double). Set when the Bouncer takes a bet.
        private readonly PerScreen<int> betOddsNumerator = new(() => 0);
        private readonly PerScreen<int> betOddsDenominator = new(() => 0);

        // The odds board is computed once per festival (first time it's needed) and then frozen, so
        // the odds quoted when betting are the same odds used at payout even if players remount.
        private readonly PerScreen<List<RacerOdds>?> postedOdds = new(() => null);

        private sealed record RacerOdds(
            string Answer,        // response key, same format Pam uses: "farmer_<id>" / "npc_<name>"
            string DisplayName,
            long? FarmerId,
            string? NpcName,
            int Numerator,
            int Denominator);

        // Bookmaker's ladder of quotable fractional odds, longest to shortest.
        private static readonly (int Num, int Den)[] OddsLadder =
        {
            (20, 1), (15, 1), (12, 1), (10, 1), (8, 1), (7, 1), (6, 1), (5, 1), (4, 1), (3, 1),
            (5, 2), (2, 1), (3, 2), (6, 5), (1, 1), (4, 5), (1, 2), (1, 3), (1, 4), (1, 6), (1, 8),
        };

        /// <summary>Spawns the Bouncer at <see cref="FestivalDefinition.BookieTile"/> (away festivals
        /// only). Reuses the shop-keeper actor plumbing, so he despawns with the stalls when the race
        /// starts.</summary>
        private void SpawnBookie()
        {
            FestivalDefinition def = Def;
            this.SpawnShopNpc(def.BookieTile, def.BookieFacing, BookieSpriteName, BookieActorName, "Bouncer");
        }

        // ====================================================================================
        // Dialogue flow
        // ====================================================================================

        private void ShowBookieDialog(NPC bookie)
        {
            if (pamGreeted.Value)
            {
                // Bet already placed, so the book is closed for this player, but the board stays up.
                this.ShowOddsBoard("Your bet's locked in. The board, in case you forgot:", thenReturnTo: null);
                return;
            }

            Response[] options =
            {
                new("bet", "Place a bet"),
                new("odds", "What are the odds?"),
                new("leave", "Not right now"),
            };
            Game1.currentLocation.createQuestionDialogue(
                "You here to place a bet or what? I've got the Odds here what are you willing to risk?",
                options,
                (_, answer) =>
                {
                    switch (answer)
                    {
                        case "odds":
                            Game1.afterDialogues = () =>
                                this.ShowOddsBoard("Read 'em quick:", thenReturnTo: bookie);
                            break;
                        case "bet":
                            Game1.afterDialogues = () => this.ShowBookieBetFlow(bookie);
                            break;
                    }
                }, bookie);
        }

        /// <summary>Shows the full odds board (every racer, including the local player). When
        /// <paramref name="thenReturnTo"/> is set, the bookie's main dialogue reopens afterwards.</summary>
        private void ShowOddsBoard(string header, NPC? thenReturnTo)
        {
            // Plain dialogue boxes render with SpriteText, whose newline character is '^'.
            List<RacerOdds> board = this.GetPostedOdds();
            string lines = string.Join("^", board.Select(o => $"{o.DisplayName}: {o.Numerator}/{o.Denominator}"));
            Game1.drawObjectDialogue($"{header}^{lines}");
            if (thenReturnTo != null)
                Game1.afterDialogues = () => this.ShowBookieDialog(thenReturnTo);
        }

        private void ShowBookieBetFlow(NPC bookie)
        {
            // Same house rule as Pam: no betting on yourself.
            List<RacerOdds> options = this.GetPostedOdds()
                .Where(o => o.FarmerId != Game1.player.UniqueMultiplayerID)
                .ToList();
            if (options.Count == 0)
            {
                Game1.drawObjectDialogue("No field to bet on. Come back when there's a race worth booking.");
                return;
            }

            showBettingMoneyBox.Value = true;
            Response[] racerResponses = options
                .Select(o => new Response(o.Answer, $"{o.DisplayName} ({o.Numerator}/{o.Denominator})"))
                .ToArray();
            Game1.currentLocation.createQuestionDialogue(
                "Pick your horse. Can't bet on yourself.",
                racerResponses,
                (_, racerAnswer) =>
                {
                    RacerOdds? pick = options.FirstOrDefault(o => o.Answer == racerAnswer);
                    if (pick == null)
                    {
                        Game1.afterDialogues = () => { showBettingMoneyBox.Value = false; };
                        return;
                    }
                    Game1.afterDialogues = () => this.AskBookieBetAmount(bookie, pick);
                }, bookie);
        }

        private void AskBookieBetAmount(NPC bookie, RacerOdds pick)
        {
            var amountOptions = new List<Response>();
            foreach (int betChoice in Def.BetAmounts)
            {
                // High-stakes bets (1000g+) only open up from year 2 onward (matches Pam's book).
                if (betChoice >= 1000 && Game1.year < 2)
                    continue;
                amountOptions.Add(new Response(betChoice.ToString(), $"{betChoice}g"));
            }
            amountOptions.Add(new Response("nevermind", "Nevermind"));

            Game1.currentLocation.createQuestionDialogue(
                $"{pick.DisplayName} at {pick.Numerator}/{pick.Denominator}. How much?",
                amountOptions.ToArray(),
                (_, amountAnswer) =>
                {
                    if (amountAnswer == "nevermind" || !int.TryParse(amountAnswer, out int amount))
                    {
                        Game1.afterDialogues = () => { showBettingMoneyBox.Value = false; };
                        return;
                    }
                    if (Game1.player.Money < amount)
                    {
                        Game1.afterDialogues = () =>
                        {
                            Game1.drawObjectDialogue("Come back when you're actually holding that much.");
                            Game1.afterDialogues = () => { showBettingMoneyBox.Value = false; };
                        };
                        return;
                    }

                    betTargetFarmerId.Value = pick.FarmerId;
                    betTargetNpcName.Value = pick.NpcName;
                    betAmount.Value = amount;
                    betOddsNumerator.Value = pick.Numerator;
                    betOddsDenominator.Value = pick.Denominator;
                    pamGreeted.Value = true; // one bet per festival, shared with Pam's book
                    Game1.player.Money -= amount;
                    Game1.playSound("purchase");
                    Game1.dayTimeMoneyBox.moneyShakeTimer = 800;

                    int payout = BookiePayout(amount, pick.Numerator, pick.Denominator);
                    Game1.afterDialogues = () =>
                    {
                        Game1.drawObjectDialogue(
                            $"{amount}g on {pick.DisplayName} at {pick.Numerator}/{pick.Denominator}. " +
                            $"Wins, and you collect {payout}g. Don't lose the ticket.");
                        Game1.afterDialogues = () => { showBettingMoneyBox.Value = false; };
                    };
                }, bookie);
        }

        /// <summary>Total collected on a winning odds bet: the stake back plus stake × odds.</summary>
        private static int BookiePayout(int stake, int numerator, int denominator) =>
            stake + stake * numerator / denominator;

        // ====================================================================================
        // Odds computation
        // ====================================================================================

        private List<RacerOdds> GetPostedOdds()
        {
            postedOdds.Value ??= this.ComputeOdds();
            return postedOdds.Value;
        }

        /// <summary>
        /// Builds the odds board for the current field: every online farmer plus the NPC racers that
        /// fill the remaining stalls (same roster logic as <see cref="BuildBetRacerResponses"/>).
        /// Each racer gets a strength score from their horse's stats; the score share becomes a win
        /// probability, converted to fair fractional odds and snapped to the bookmaker's ladder.
        /// </summary>
        private List<RacerOdds> ComputeOdds()
        {
            var entries = new List<(string Answer, string Name, long? FarmerId, string? NpcName, float Strength)>();

            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                entries.Add((
                    $"farmer_{farmer.UniqueMultiplayerID}",
                    farmer.Name,
                    farmer.UniqueMultiplayerID,
                    null,
                    this.GetFarmerStrength(farmer)));
            }

            int playerCount = System.Math.Max(1, Game1.getOnlineFarmers().Count());
            int npcSlots = System.Math.Min(Def.NpcRiderNames.Length, System.Math.Max(0, MaxRacers - playerCount));
            for (int i = 0; i < npcSlots; i++)
            {
                entries.Add((
                    $"npc_{Def.NpcRiderNames[i]}",
                    Def.NpcRiderNames[i],
                    null,
                    Def.NpcRiderNames[i],
                    RacerStrength(Def.NpcRiderSpeeds[i], Def.NpcRiderSprints[i], Def.NpcRiderJumps[i])));
            }

            float total = entries.Sum(e => e.Strength);
            var board = new List<RacerOdds>();
            foreach (var e in entries)
            {
                float p = e.Strength / total;
                (int num, int den) = SnapToLadder((1f - p) / p);
                board.Add(new RacerOdds(e.Answer, e.Name, e.FarmerId, e.NpcName, num, den));
            }

            Logger.LogVerbose("Bookie odds board: " +
                string.Join(", ", board.Select(o => $"{o.DisplayName} {o.Numerator}/{o.Denominator}")));
            return board;
        }

        /// <summary>Snaps a fair odds multiplier (winnings per 1g staked) to the nearest quotable
        /// fraction on the ladder, comparing ratios so 1/8 vs 1/6 is weighed the same as 15/1 vs 20/1.</summary>
        private static (int Num, int Den) SnapToLadder(float fairValue)
        {
            fairValue = System.Math.Max(fairValue, 0.01f);
            var best = OddsLadder[0];
            double bestError = double.MaxValue;
            foreach (var rung in OddsLadder)
            {
                double error = System.Math.Abs(System.Math.Log(fairValue / ((double)rung.Num / rung.Den)));
                if (error < bestError)
                {
                    bestError = error;
                    best = rung;
                }
            }
            return best;
        }

        // A horse's race strength. The constant is the base pace every horse shares (race speed is
        // 5 + speed/20 tiles/sec, so stats shift the outcome rather than dominate it); speed matters
        // most, sprint bursts about half as much, jumps only on courses that have obstacles.
        private static float RacerStrength(int speed, int sprint, int jump) =>
            100f + speed + sprint / 2f + jump / 4f;

        /// <summary>Strength of a farmer's entry: the best horse among their mount and everything
        /// they brought (bus claims). A farmer with no horse yet is booked at borrowed-horse
        /// baseline strength.</summary>
        private float GetFarmerStrength(Farmer farmer)
        {
            float best = RacerStrength(0, 0, 0);

            if (farmer.mount != null)
                best = System.Math.Max(best, HorseStrength(farmer.mount));
            if (farmer.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID && competitor.Value != null)
                best = System.Math.Max(best, HorseStrength(competitor.Value));

            List<FarmAnimal> barnHorses = HorseHelper.GetAllBarnHorses();
            foreach (long animalId in BusHorseClaims
                .Where(kv => kv.Value == farmer.UniqueMultiplayerID)
                .Select(kv => kv.Key))
            {
                FarmAnimal? animal = barnHorses.FirstOrDefault(a => a.myID.Value == animalId);
                if (animal != null)
                {
                    var stats = animal.GetHorseStats();
                    best = System.Math.Max(best, RacerStrength(stats.TotalSpeed, stats.TotalSprint, stats.TotalJump));
                }
            }

            return best;
        }

        private static float HorseStrength(Horse mount)
        {
            FarmAnimal? animal = HorseHelper.GetFarmAnimalForHorse(mount);
            if (animal != null)
            {
                var stats = animal.GetHorseStats();
                return RacerStrength(stats.TotalSpeed, stats.TotalSprint, stats.TotalJump);
            }
            // Temporary festival horses (bus/borrowed) carry their stats in modData.
            int speed = mount.modData.TryGetValue(HorseHelper.BorrowedSpeedKey, out string sv) && int.TryParse(sv, out int s) ? s : 0;
            int sprint = mount.modData.TryGetValue(HorseHelper.BorrowedSprintKey, out string pv) && int.TryParse(pv, out int p) ? p : 0;
            int jump = mount.modData.TryGetValue(HorseHelper.BorrowedJumpKey, out string jv) && int.TryParse(jv, out int j) ? j : 0;
            return RacerStrength(speed, sprint, jump);
        }
    }
}
