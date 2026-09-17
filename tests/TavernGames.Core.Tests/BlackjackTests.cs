using TavernGames.Core.Cards;
using TavernGames.Core.Games.Blackjack;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Tests;

public class BlackjackTests
{
    // Deal order for one player: player, dealer up, player, dealer hole, then any draws.
    private static BlackjackGame Solo(string deck, int chips = 500, int rounds = 10, int minBet = 10)
    {
        var game = new BlackjackGame(() => Deck.Stacked(deck.Split(' ')), chips, rounds, minBet);
        game.AddPlayer("a", "Alice");
        game.StartGame();
        return game;
    }

    private static BlackjackGame Pair(string deck, int rounds = 10)
    {
        // Deal order for two: a, b, dealer up, a, b, dealer hole.
        var game = new BlackjackGame(() => Deck.Stacked(deck.Split(' ')), 500, rounds, 10);
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.StartGame();
        return game;
    }

    private static int Chips(BlackjackGame game, string id) => game.Players.First(p => p.Id == id).Chips;
    private static Card[] Cards(string codes) => codes.Split(' ').Select(Card.Parse).ToArray();

    [Theory]
    [InlineData("AS KD", 21, true)]
    [InlineData("AS AD 9C", 21, true)]
    [InlineData("AS 6D KC", 17, false)]
    [InlineData("AS AD AC AH", 14, true)]
    [InlineData("KS QD 5C", 25, false)]
    public void Totals_CountAcesAsElevenOnlyWhileItHelps(string cards, int total, bool soft) =>
        Assert.Equal((total, soft), BjMath.Total(Cards(cards)));

    [Fact]
    public void Bets_MustBeWithinTheMinimumAndYourStack_AndOnlyOncePerRound()
    {
        var game = Pair("TS 9S 7D 9H 8H TC");

        Assert.Throws<InvalidOperationException>(() => game.PlaceBet("a", 5));
        Assert.Throws<InvalidOperationException>(() => game.PlaceBet("a", 501));
        game.PlaceBet("a", 50);
        Assert.Equal(450, Chips(game, "a"));
        Assert.Throws<InvalidOperationException>(() => game.PlaceBet("a", 50));
        Assert.Throws<InvalidOperationException>(() => game.Hit("a")); // nothing dealt until everyone has bet
    }

    [Fact]
    public void CardsAreDealt_OnceEveryBetIsIn_AndPlayGoesInSeatOrder()
    {
        var game = Pair("TS 9S 7D 9H 8H TC");
        game.PlaceBet("b", 20);
        Assert.True(game.IsBetting);

        var events = game.PlaceBet("a", 50);

        Assert.Contains(events, e => e.Kind == BjEventKind.Dealt);
        Assert.Equal("a", game.HandToPlay?.Id);
        Assert.Throws<InvalidOperationException>(() => game.Stand("b")); // not Bob's turn yet
        game.Stand("a");
        Assert.Equal("b", game.HandToPlay?.Id);
    }

    [Fact]
    public void TheHoleCard_StaysHidden_UntilTheDealerPlays()
    {
        var game = Solo("TS 7D 9H KC");
        var dealt = game.PlaceBet("a", 50).Single(e => e.Kind == BjEventKind.Dealt);

        Assert.Equal(["7D", Card.HiddenCode], dealt.Table.DealerCards);
        Assert.Equal(7, dealt.Table.DealerTotal);
        Assert.DoesNotContain("KC", new BjDealt(dealt.Table).Serialize()); // and it never reaches the wire

        var reveal = game.Stand("a").First(e => e.Kind == BjEventKind.DealerPlayed);
        Assert.True(reveal.Reveal);
        Assert.Equal(["7D", "KC"], reveal.Table.DealerCards);
        Assert.Equal(17, reveal.Table.DealerTotal);
    }

    [Fact]
    public void BeatingTheDealer_PaysEvenMoney_AndOpensTheNextRound()
    {
        var game = Solo("TS 7D 9H TC"); // 19 against 17
        game.PlaceBet("a", 50);

        var kinds = game.Stand("a").Select(e => e.Kind).ToList();

        Assert.Equal(
            [BjEventKind.Played, BjEventKind.DealerPlayed, BjEventKind.RoundSettled, BjEventKind.BettingOpened], kinds);
        Assert.Equal(550, Chips(game, "a"));
        Assert.Equal(2, game.Round);
    }

    [Fact]
    public void Busting_LosesTheBet_AndTheDealerDoesNotBotherDrawing()
    {
        var game = Solo("TS 5D 6H TC 9C"); // 16, hits a 9; dealer sits on 15
        game.PlaceBet("a", 50);

        var events = game.Hit("a");

        var settled = events.Single(e => e.Kind == BjEventKind.RoundSettled);
        Assert.Equal(BjOutcome.Bust, settled.Payouts!.Single().Outcome);
        Assert.Equal(2, settled.Table.DealerCards.Length);
        Assert.Equal(450, Chips(game, "a"));
    }

    [Fact]
    public void ANaturalBlackjack_PaysThreeToTwo_WithoutWaitingForAMove()
    {
        var game = Solo("AS 7D KH TC");

        var events = game.PlaceBet("a", 50);

        var payout = events.Single(e => e.Kind == BjEventKind.RoundSettled).Payouts!.Single();
        Assert.Equal((BjOutcome.Blackjack, 75), (payout.Outcome, payout.Net));
        Assert.Equal(575, Chips(game, "a"));
    }

    [Fact]
    public void ADealerBlackjack_EndsTheRoundAtOnce_AndOnlyAPlayerBlackjackPushes()
    {
        var game = Pair("TS AS AD 9H KH KC"); // a: 19, b: blackjack, dealer: blackjack
        game.PlaceBet("a", 50);
        var events = game.PlaceBet("b", 50);

        var payouts = events.Single(e => e.Kind == BjEventKind.RoundSettled).Payouts!;
        Assert.Equal(BjOutcome.Lose, payouts.Single(p => p.PlayerId == "a").Outcome);
        Assert.Equal(BjOutcome.Push, payouts.Single(p => p.PlayerId == "b").Outcome);
        Assert.Equal((450, 500), (Chips(game, "a"), Chips(game, "b")));
    }

    [Fact]
    public void DoublingDown_DoublesTheBet_TakesOneCard_AndStands()
    {
        var game = Solo("5S 6D 6H TC 9C KD"); // 11 doubles into 20; dealer 16 draws a king and busts
        game.PlaceBet("a", 50);

        var events = game.DoubleDown("a");

        var played = events.First(e => e.Kind == BjEventKind.Played);
        Assert.Equal(("double", "9C", 100), (played.Action, played.Card, played.Amount));
        Assert.Equal(600, Chips(game, "a")); // 500 - 100 staked + 200 back
    }

    [Fact]
    public void DoublingDown_NeedsTwoCardsAndTheChips()
    {
        var game = Solo("2S 6D 3H TC 4C 5C");
        game.PlaceBet("a", 300);
        Assert.Throws<InvalidOperationException>(() => game.DoubleDown("a")); // only 200 left behind a 300 bet

        var second = Solo("2S 6D 3H TC 4C 5C");
        second.PlaceBet("a", 50);
        second.Hit("a");
        Assert.Throws<InvalidOperationException>(() => second.DoubleDown("a")); // already took a card
    }

    [Fact]
    public void TheDealer_StandsOnSoftSeventeen_AndDrawsBelowIt()
    {
        var soft = Solo("TS AD 9H 6C 5C"); // dealer A+6
        soft.PlaceBet("a", 50);
        var settled = soft.Stand("a").Single(e => e.Kind == BjEventKind.RoundSettled);
        Assert.Equal(2, settled.Table.DealerCards.Length);
        Assert.Equal(550, Chips(soft, "a"));

        var low = Solo("TS 6D 9H TC 5C"); // dealer 16 must draw: 21 beats 19
        low.PlaceBet("a", 50);
        low.Stand("a");
        Assert.Equal(450, Chips(low, "a"));
    }

    [Fact]
    public void EqualTotals_Push_AndTheBetComesBack()
    {
        var game = Solo("TS TD 8H 8C");
        game.PlaceBet("a", 50);
        game.Stand("a");
        Assert.Equal(500, Chips(game, "a"));
    }

    [Fact]
    public void HittingTwentyOne_StandsAutomatically()
    {
        var game = Solo("5S 9D 6H 8C TC"); // 11 + 10
        game.PlaceBet("a", 50);

        var events = game.Hit("a");

        Assert.Contains(events, e => e.Kind == BjEventKind.RoundSettled); // no further input needed
    }

    [Fact]
    public void AfterTheLastRound_TheBiggestStackWins()
    {
        var game = Pair("TS 9S 7D 9H 5H TC", rounds: 1); // a: 19 wins, b: 14 stands and loses to 17
        game.PlaceBet("a", 50);
        game.PlaceBet("b", 50);
        game.Stand("a");

        var events = game.Stand("b");

        Assert.Equal("a", events.Single(e => e.Kind == BjEventKind.GameOver).WinnerId);
        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal(1, events.Last().Table.Round); // never reports a round past the last
    }

    [Fact]
    public void APlayerWhoCannotCoverTheMinimum_IsOut_AndTheGameEndsWhenEveryoneIs()
    {
        var game = Solo("TS TD 6H 9C", chips: 10, minBet: 10); // 16 against 19
        game.PlaceBet("a", 10);

        var events = game.Stand("a");

        Assert.True(game.Players.Single().Out);
        Assert.Contains(events, e => e.Kind == BjEventKind.GameOver);
    }

    [Fact]
    public void SomeoneLeavingMidBetting_DoesNotStallTheDeal()
    {
        var game = Pair("TS 7D 9H TC");
        game.PlaceBet("a", 50);

        var events = game.RemovePlayer("b"); // Bob was the only one still owing a bet

        Assert.Contains(events, e => e.Kind == BjEventKind.Dealt);
        Assert.Equal("a", game.HandToPlay?.Id);
    }

    [Fact]
    public void SomeoneLeavingOnTheirTurn_PassesPlayOn()
    {
        var game = Pair("TS 9S 7D 9H 8H TC");
        game.PlaceBet("a", 50);
        game.PlaceBet("b", 50);

        game.RemovePlayer("a");

        Assert.Equal("b", game.HandToPlay?.Id);
    }

    // ------------------------------------------------------------------ module

    [Fact]
    public void TheModule_LetsTheDealerPlayOneCardAtATime_ThenGivesTimeToReadTheResult()
    {
        var module = new BlackjackModule(() => Deck.Stacked("TS 6D 9H TC 5C".Split(' ')), 500, 10, 10);
        module.AddPlayer("a", "Alice", isBot: false);
        module.Start();
        module.Handle("a", new BjBet(50));

        var kinds = module.Handle("a", new BjStand()).Select(e => e switch
        {
            ToAll { Message: BjPlayed } => "played",
            ToAll { Message: BjDealerPlayed } => "dealer",
            ToAll { Message: BjRoundSettled } => "settled",
            ToAll { Message: BjBettingOpened } => "betting",
            Pause { Kind: PauseKind.Beat } => "beat",
            Pause { Kind: PauseKind.RoundBreak } => "break",
            _ => "other",
        }).ToList();

        Assert.Equal(["played", "beat", "dealer", "beat", "dealer", "beat", "settled", "break", "betting"], kinds);
    }

    [Fact]
    public void Bots_BetWithoutWaitingForHumans()
    {
        var module = new BlackjackModule(Deck.Shuffled, 500, 10, 10);
        module.AddPlayer("human", "Mina", isBot: false);
        module.AddPlayer("bot", "Gilbot", isBot: true);
        module.Start();

        Assert.Equal("bot", module.CurrentActorId); // the human sits first but hasn't bet; the bot goes anyway
        module.Handle("bot", module.DecideBotMove("bot", new Random(1))!);
        Assert.Equal("human", module.CurrentActorId);
    }

    [Fact]
    public void Bots_AlwaysProduceLegalMoves_AndGamesFinish()
    {
        var rng = new Random(5);
        for (var trial = 0; trial < 40; trial++)
        {
            var module = new BlackjackModule(Deck.Shuffled, 200, 8, 10);
            for (var i = 0; i < 1 + trial % BlackjackGame.MaxPlayers; i++)
                module.AddPlayer($"bot{i}", $"Bot {i}", isBot: true);
            module.Start();

            for (var step = 0; step < 5000 && module.Phase == GamePhase.Playing; step++)
            {
                var actor = module.CurrentActorId;
                Assert.NotNull(actor);
                var move = module.DecideBotMove(actor!, rng);
                Assert.NotNull(move);
                module.Handle(actor!, move!); // throws if the bot chose something illegal
            }

            Assert.Equal(GamePhase.GameOver, module.Phase);
        }
    }

    [Fact]
    public void BlackjackMessages_RoundTripOnTheWire()
    {
        var module = new BlackjackModule(() => Deck.Stacked("TS 7D 9H TC".Split(' ')), 500, 10, 10);
        module.AddPlayer("a", "Alice", isBot: false);
        module.Start();

        foreach (var emit in module.Handle("a", new BjBet(50)).OfType<ToAll>())
            Assert.IsType(emit.Message.GetType(), NetMessage.Deserialize(emit.Message.Serialize()));
        Assert.IsType<BjDouble>(NetMessage.Deserialize(new BjDouble().Serialize()));
    }
}
