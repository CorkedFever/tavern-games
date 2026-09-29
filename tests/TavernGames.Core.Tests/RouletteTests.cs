using TavernGames.Core.Games.Roulette;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Tests;

public class RouletteTests
{
    /// <summary>A wheel that lands where the test says, spin after spin.</summary>
    private static Func<int> Lands(params int[] numbers)
    {
        var i = 0;
        return () => numbers[i++ % numbers.Length];
    }

    private static RouletteGame Solo(int number, int chips = 500, int rounds = 10, int minBet = 5)
    {
        var game = new RouletteGame(Lands(number), chips, rounds, minBet);
        game.AddPlayer("a", "Alice");
        game.StartGame();
        return game;
    }

    private static RouletteGame Pair(int number, int rounds = 10)
    {
        var game = new RouletteGame(Lands(number), 500, rounds, 5);
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.StartGame();
        return game;
    }

    private static RoulettePlayer Seat(RouletteGame game, string id) => game.Players.First(p => p.Id == id);

    [Fact]
    public void TheWheel_HasEveryPocketOnce_AndEighteenOfEachColour()
    {
        Assert.Equal(37, RouletteGame.WheelOrder.Count);
        Assert.Equal(Enumerable.Range(0, 37), RouletteGame.WheelOrder.Order());
        Assert.Equal(18, Enumerable.Range(0, 37).Count(RouletteGame.IsRed));
        Assert.Equal(18, Enumerable.Range(0, 37).Count(RouletteGame.IsBlack));
        Assert.False(RouletteGame.IsRed(0) || RouletteGame.IsBlack(0));
    }

    [Theory]
    [InlineData(RouletteBetKind.Straight, 17, 17, 36)]
    [InlineData(RouletteBetKind.Straight, 17, 18, 0)]
    [InlineData(RouletteBetKind.Straight, 0, 0, 36)]
    [InlineData(RouletteBetKind.Red, 0, 1, 2)]
    [InlineData(RouletteBetKind.Red, 0, 0, 0)]
    [InlineData(RouletteBetKind.Black, 0, 0, 0)]
    [InlineData(RouletteBetKind.Black, 0, 17, 2)]
    [InlineData(RouletteBetKind.Odd, 0, 0, 0)]
    [InlineData(RouletteBetKind.Even, 0, 36, 2)]
    [InlineData(RouletteBetKind.Low, 0, 18, 2)]
    [InlineData(RouletteBetKind.High, 0, 18, 0)]
    [InlineData(RouletteBetKind.Dozen, 2, 13, 3)]
    [InlineData(RouletteBetKind.Dozen, 3, 36, 3)]
    [InlineData(RouletteBetKind.Column, 1, 34, 3)]
    [InlineData(RouletteBetKind.Column, 3, 3, 3)]
    [InlineData(RouletteBetKind.Column, 2, 3, 0)]
    public void Payouts_FollowTheTable(RouletteBetKind kind, int pick, int number, int multiplier) =>
        Assert.Equal(multiplier * 10, RouletteGame.Returned(new RouletteBet(kind, pick, 10), number));

    [Fact]
    public void Bets_MustCoverTheMinimum_FitTheStack_AndAddUpOnASpot()
    {
        var game = Solo(17, chips: 100);

        Assert.Throws<InvalidOperationException>(() => game.Place("a", RouletteBetKind.Red, 0, 4));
        Assert.Throws<InvalidOperationException>(() => game.Place("a", RouletteBetKind.Red, 0, 101));
        Assert.Throws<InvalidOperationException>(() => game.Place("a", RouletteBetKind.Straight, 37, 5));
        Assert.Throws<InvalidOperationException>(() => game.Place("a", RouletteBetKind.Dozen, 4, 5));

        game.Place("a", RouletteBetKind.Red, 0, 10);
        var placed = game.Place("a", RouletteBetKind.Red, 0, 10).Single();
        game.Place("a", RouletteBetKind.Straight, 17, 5);

        Assert.Equal(new RouletteBet(RouletteBetKind.Red, 0, 20), placed.Bet);
        Assert.Equal(2, Seat(game, "a").Bets.Count);
        Assert.Equal(75, Seat(game, "a").Chips);

        game.Clear("a");
        Assert.Empty(Seat(game, "a").Bets);
        Assert.Equal(100, Seat(game, "a").Chips);
    }

    [Fact]
    public void TheWheel_SpinsOnceEveryoneIsDone_AndPaysTheStacks()
    {
        var game = Pair(17); // 17 is black
        game.Place("a", RouletteBetKind.Straight, 17, 5);
        game.Place("b", RouletteBetKind.Black, 0, 10);

        Assert.DoesNotContain(game.Done("a"), e => e.Kind == RouletteEventKind.Spun); // Bob is still betting
        var kinds = game.Done("b").Select(e => e.Kind).ToList();

        Assert.Equal([RouletteEventKind.Ready, RouletteEventKind.Spun, RouletteEventKind.Settled, RouletteEventKind.BettingOpened], kinds);
        Assert.Equal(675, Seat(game, "a").Chips); // 500 - 5 + 5 * 36
        Assert.Equal(510, Seat(game, "b").Chips); // 500 - 10 + 10 * 2
        Assert.Equal(17, game.LastNumber);
        Assert.Equal([17], game.History);
        Assert.Equal(2, game.Round);
    }

    [Fact]
    public void TheSettlement_SaysWhatEachSeatStakedAndGotBack()
    {
        var game = Pair(17);
        game.Place("a", RouletteBetKind.Straight, 17, 5);
        game.Place("a", RouletteBetKind.Red, 0, 10);
        game.Done("a");

        var settled = game.Done("b").Single(e => e.Kind == RouletteEventKind.Settled);

        var payout = Assert.Single(settled.Payouts!); // Bob bet nothing and isn't listed
        Assert.Equal(("a", 15, 180), (payout.PlayerId, payout.Staked, payout.Returned));
        Assert.Equal(17, settled.Number);
        Assert.False(settled.Table.Betting);
        Assert.Equal(2, settled.Table.Seats.Single(s => s.PlayerId == "a").Bets.Length); // still on the layout to be looked at
    }

    [Fact]
    public void Zero_TakesEveryOutsideBet()
    {
        var game = Solo(0);
        game.Place("a", RouletteBetKind.Red, 0, 10);
        game.Place("a", RouletteBetKind.Low, 0, 10);
        game.Place("a", RouletteBetKind.Dozen, 1, 10);
        game.Place("a", RouletteBetKind.Straight, 0, 5);

        game.Done("a");

        Assert.Equal(645, Seat(game, "a").Chips); // 500 - 35 + 5 * 36
    }

    [Fact]
    public void DoneWithNoBets_SitsTheSpinOut()
    {
        var game = Solo(5);

        var settled = game.Done("a").Single(e => e.Kind == RouletteEventKind.Settled);

        Assert.Empty(settled.Payouts!);
        Assert.Equal(500, Seat(game, "a").Chips);
        Assert.Equal(2, game.Round);
    }

    [Fact]
    public void OnceYourBetsAreDown_TheyStayDown()
    {
        var game = Pair(5);
        game.Place("a", RouletteBetKind.Odd, 0, 5);
        game.Done("a");

        Assert.Throws<InvalidOperationException>(() => game.Place("a", RouletteBetKind.Even, 0, 5));
        Assert.Throws<InvalidOperationException>(() => game.Clear("a"));
        Assert.Throws<InvalidOperationException>(() => game.Done("a"));
    }

    [Fact]
    public void TheGame_EndsAfterTheLastSpin_WithTheRichestSeatWinning()
    {
        var game = Pair(17, rounds: 2);
        game.Place("a", RouletteBetKind.Straight, 17, 5);
        game.Done("a");
        game.Done("b");
        game.Done("a");

        var over = game.Done("b").Single(e => e.Kind == RouletteEventKind.GameOver);

        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal("a", over.WinnerId);
        Assert.Equal("a", game.WinnerId);
    }

    [Fact]
    public void ASeatThatCannotCoverTheMinimum_IsOut_AndAloneThatEndsTheGame()
    {
        var game = Solo(5, chips: 5, rounds: 5);
        game.Place("a", RouletteBetKind.Straight, 7, 5);

        var kinds = game.Done("a").Select(e => e.Kind).ToList();

        Assert.Equal([RouletteEventKind.Ready, RouletteEventKind.Spun, RouletteEventKind.Settled, RouletteEventKind.GameOver], kinds);
        Assert.True(Seat(game, "a").Out);
        Assert.Equal(GamePhase.GameOver, game.Phase);
    }

    [Fact]
    public void SomeoneLeaving_WhileTheOthersWaitOnThem_LetsTheWheelTurn()
    {
        var game = Pair(5);
        game.Done("a");

        var events = game.RemovePlayer("b");

        Assert.Contains(events, e => e.Kind == RouletteEventKind.Spun);
        Assert.Single(game.Players);
    }

    [Fact]
    public void TheHistory_KeepsTheLastTwelve_NewestFirst()
    {
        var game = new RouletteGame(Lands(Enumerable.Range(1, 14).ToArray()), 500, 20, 5);
        game.AddPlayer("a", "Alice");
        game.StartGame();

        for (var spin = 0; spin < 14; spin++)
            game.Done("a");

        Assert.Equal(12, game.History.Count);
        Assert.Equal(14, game.History[0]);
        Assert.Equal(3, game.History[^1]);
    }

    // ------------------------------------------------------------------ module

    [Fact]
    public void TheModule_GivesTheWheelTimeToTurn_ThenTimeToReadTheResult()
    {
        var module = new RouletteModule(Lands(17), 500, 10, 5);
        module.AddPlayer("a", "Alice", isBot: false);
        module.Start();
        module.Handle("a", new RoulettePlace(RouletteBetKind.Red, 0, 5));

        var kinds = module.Handle("a", new RouletteDone()).Select(e => e switch
        {
            ToAll { Message: RouletteReady } => "ready",
            ToAll { Message: RouletteSpun } => "spun",
            ToAll { Message: RouletteSettled } => "settled",
            ToAll { Message: RouletteBettingOpened } => "betting",
            Pause { Kind: PauseKind.Beat } => "beat",
            Pause { Kind: PauseKind.RoundBreak } => "break",
            _ => "other",
        }).ToList();

        Assert.Equal(["ready", "beat", "spun", "break", "settled", "break", "betting"], kinds);
    }

    [Fact]
    public void Bots_BetWithoutWaitingForHumans_AndSayWhenTheyAreDone()
    {
        var module = new RouletteModule(Lands(5), 500, 10, 5);
        module.AddPlayer("human", "Mina", isBot: false);
        module.AddPlayer("bot", "Gilbot", isBot: true);
        module.Start();
        var rng = new Random(1);

        Assert.Equal("bot", module.CurrentActorId); // the human sits first but hasn't bet; the bot goes anyway

        var placed = 0;
        NetMessage move;
        do
        {
            move = module.DecideBotMove("bot", rng)!;
            if (move is RoulettePlace) placed++;
            module.Handle("bot", move);
        }
        while (move is not RouletteDone);

        Assert.InRange(placed, 1, 3);
        Assert.Equal("human", module.CurrentActorId);
        Assert.Null(module.DecideBotMove("bot", rng)); // nothing more to say until the next round
    }

    [Fact]
    public void Bots_AlwaysProduceLegalMoves_AndGamesFinish()
    {
        var rng = new Random(5);
        for (var trial = 0; trial < 40; trial++)
        {
            var wheel = new Random(trial);
            var module = new RouletteModule(() => wheel.Next(RouletteGame.Pockets), 200, 8, 5);
            for (var i = 0; i < 1 + trial % RouletteGame.MaxPlayers; i++)
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
    public void RouletteMessages_RoundTripOnTheWire()
    {
        var module = new RouletteModule(Lands(17), 500, 10, 5);
        module.AddPlayer("a", "Alice", isBot: false);
        module.Start();
        module.Handle("a", new RoulettePlace(RouletteBetKind.Straight, 17, 5));

        foreach (var emit in module.Handle("a", new RouletteDone()).OfType<ToAll>())
            Assert.IsType(emit.Message.GetType(), NetMessage.Deserialize(emit.Message.Serialize()));

        var place = Assert.IsType<RoulettePlace>(NetMessage.Deserialize(new RoulettePlace(RouletteBetKind.Dozen, 2, 10).Serialize()));
        Assert.Equal((RouletteBetKind.Dozen, 2, 10), (place.Kind, place.Pick, place.Amount));
        Assert.Contains("\"$type\":\"roulette.place\"", place.Serialize());
    }
}
