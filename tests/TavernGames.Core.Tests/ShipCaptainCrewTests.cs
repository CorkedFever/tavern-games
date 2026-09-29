using TavernGames.Core.Games.ShipCaptainCrew;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Tests;

public class ShipCaptainCrewTests
{
    // The roller hands out faces in order to whichever dice are thrown: all five on a fresh
    // turn, only the loose ones after that, always in die order.
    private static ShipCaptainCrewGame Pair(int roundsToWin, params int[] faces)
    {
        var game = new ShipCaptainCrewGame(new ScriptedRoller(faces), roundsToWin);
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.StartGame();
        return game;
    }

    private static ShipCaptainCrewGame Trio(int roundsToWin, params int[] faces)
    {
        var game = new ShipCaptainCrewGame(new ScriptedRoller(faces), roundsToWin);
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.AddPlayer("c", "Cid");
        game.StartGame();
        return game;
    }

    private static int[] Ones(int count) => Enumerable.Repeat(1, count).ToArray();

    private static SccPlayer Seat(ShipCaptainCrewGame game, string id) => game.Players.First(p => p.Id == id);

    [Fact]
    public void TheShipComesFirst_ThenTheCaptain_ThenTheCrew_EvenInOneThrow()
    {
        var game = Pair(3, 5, 4, 6, 1, 1);

        game.Roll("a");

        var a = Seat(game, "a");
        Assert.True(a.Ship && a.Captain && a.Crew);
        Assert.Equal([true, true, true, false, false], a.Kept); // the 5 and the 4 count because the 6 came with them
        Assert.Equal([1, 1], a.Cargo);
        Assert.Equal(2, a.Score);
        Assert.Equal(2, a.RollsLeft);
    }

    [Fact]
    public void ACaptainThrownBeforeTheShip_IsLostWithTheNextThrow()
    {
        var game = Pair(3, 5, 4, 3, 3, 3, 6, 1, 1, 1, 1);

        game.Roll("a");
        Assert.DoesNotContain(true, Seat(game, "a").Kept);

        game.Roll("a");
        var a = Seat(game, "a");
        Assert.True(a.Ship);
        Assert.False(a.Captain);
        Assert.Equal([6, 1, 1, 1, 1], a.Dice); // the whole hand was thrown again
        Assert.Equal(1, a.RollsLeft);
    }

    [Fact]
    public void WithTheCrewAboard_OnlyTheCargoIsThrownAgain()
    {
        var game = Pair(3, 6, 5, 4, 1, 1, 6, 6);
        game.Roll("a");
        Assert.Equal(2, Seat(game, "a").Score);

        game.Roll("a");

        var a = Seat(game, "a");
        Assert.Equal([6, 5, 4, 6, 6], a.Dice);
        Assert.Equal([true, true, true, false, false], a.Kept);
        Assert.Equal(12, a.Score);
        Assert.Equal(1, a.RollsLeft);
    }

    [Fact]
    public void Holding_NeedsAThrow_AndACrew()
    {
        var game = Pair(3, Ones(15));

        Assert.Throws<InvalidOperationException>(() => game.Hold("a"));
        game.Roll("a");
        Assert.Throws<InvalidOperationException>(() => game.Hold("a"));
        Assert.Throws<InvalidOperationException>(() => game.Roll("b")); // not Bob's turn
    }

    [Fact]
    public void ThreeThrowsWithNoCrew_EndTheTurnWithNothing()
    {
        var game = Pair(3, Ones(15));
        game.Roll("a");
        game.Roll("a");

        var kinds = game.Roll("a").Select(e => e.Kind).ToList();

        Assert.Equal([SccEventKind.Rolled, SccEventKind.Held, SccEventKind.TurnStarted], kinds);
        Assert.True(Seat(game, "a").Done);
        Assert.Equal(0, Seat(game, "a").Score);
        Assert.Equal("b", game.Current?.Id);
    }

    [Fact]
    public void TheBestCargo_TakesTheRound_AndTheGameAtTheTarget()
    {
        var game = Pair(1, 6, 5, 4, 6, 6, 6, 5, 4, 1, 1);
        game.Roll("a");
        Assert.Equal([SccEventKind.Held, SccEventKind.TurnStarted], game.Hold("a").Select(e => e.Kind));
        game.Roll("b");

        var events = game.Hold("b");

        Assert.Equal([SccEventKind.Held, SccEventKind.RoundEnded, SccEventKind.GameOver], events.Select(e => e.Kind));
        var round = events.Single(e => e.Kind == SccEventKind.RoundEnded);
        Assert.Equal(("a", 12), (round.WinnerId, round.Score));
        Assert.Equal(1, Seat(game, "a").Points);
        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal("a", game.WinnerId);
    }

    [Fact]
    public void ATie_IsRolledOff_ByTheTiedSeatsAlone()
    {
        var game = Trio(3, [6, 5, 4, 3, 3, 6, 5, 4, 2, 4, .. Ones(15), 6, 5, 4, 1, 1, 6, 5, 4, 2, 2]);
        game.Roll("a");
        game.Hold("a"); // cargo 6
        game.Roll("b");
        game.Hold("b"); // cargo 6
        game.Roll("c");
        game.Roll("c");

        var events = game.Roll("c"); // nothing, and the round is decided

        var round = events.Single(e => e.Kind == SccEventKind.RoundEnded);
        Assert.True(round.TieBreak);
        Assert.Equal(["a", "b"], round.Tied!);
        Assert.Equal(["a", "b"], game.Contenders);
        Assert.False(game.Snapshot().Seats.Single(s => s.PlayerId == "c").InRound);
        Assert.Equal("a", game.Current?.Id);
        Assert.Equal(2, game.Round);

        game.Roll("a");
        game.Hold("a"); // cargo 2
        game.Roll("b");
        var decided = game.Hold("b").Single(e => e.Kind == SccEventKind.RoundEnded); // cargo 4

        Assert.Equal(("b", 4), (decided.WinnerId, decided.Score));
        Assert.Equal(1, Seat(game, "b").Points);
        Assert.Equal(["a", "b", "c"], game.Contenders); // everyone is back in for round three
        Assert.Equal(3, game.Round);
    }

    [Fact]
    public void NobodyWithACrew_MeansNobodyWins_AndTheRoundIsThrownAgain()
    {
        var game = Pair(3, Ones(30));
        for (var i = 0; i < 5; i++) game.Roll(game.Current!.Id);

        var events = game.Roll("b");

        var round = events.Single(e => e.Kind == SccEventKind.RoundEnded);
        Assert.Null(round.WinnerId);
        Assert.False(round.TieBreak);
        Assert.Equal(SccEventKind.TurnStarted, events[^1].Kind);
        Assert.Equal(2, game.Round);
        Assert.Equal(0, Seat(game, "a").Points + Seat(game, "b").Points);
    }

    [Fact]
    public void TheLeader_IsTheBestHeldCargo_AndTheEarlierSeatKeepsATie()
    {
        var game = Trio(3, 6, 5, 4, 3, 3, 6, 5, 4, 4, 2);
        game.Roll("a");
        game.Hold("a");
        Assert.Equal((6, "a"), game.Leader);

        game.Roll("b");
        game.Hold("b");

        Assert.Equal((6, "a"), game.Leader);
        Assert.Equal(6, game.Snapshot().LeaderScore);
    }

    [Fact]
    public void LeavingOnYourTurn_PassesTheDice_AndLeavingAlone_EndsTheGame()
    {
        var trio = Trio(3, Ones(30));
        var events = trio.RemovePlayer("a");
        Assert.Equal(SccEventKind.TurnStarted, Assert.Single(events).Kind);
        Assert.Equal("b", trio.Current?.Id);
        Assert.Equal(["b", "c"], trio.Contenders);

        var pair = Pair(3, Ones(30));
        var over = pair.RemovePlayer("b");
        Assert.Equal(SccEventKind.GameOver, Assert.Single(over).Kind);
        Assert.Equal("a", pair.WinnerId);
    }

    // ------------------------------------------------------------------ module

    [Fact]
    public void TheModule_BreathesAfterATurn_AndBreaksAfterARound()
    {
        var module = new ShipCaptainCrewModule(new ScriptedRoller(6, 5, 4, 6, 6, 6, 5, 4, 1, 1), 3);
        module.AddPlayer("a", "Alice", isBot: false);
        module.AddPlayer("b", "Bob", isBot: false);
        module.Start();
        module.Handle("a", new SccRoll());

        static string Kind(Emit e) => e switch
        {
            ToAll { Message: SccHeld } => "held",
            ToAll { Message: SccTurnStarted } => "turn",
            ToAll { Message: SccRoundEnded } => "roundEnded",
            Pause { Kind: PauseKind.Beat } => "beat",
            Pause { Kind: PauseKind.RoundBreak } => "break",
            _ => "other",
        };

        Assert.Equal(["held", "beat", "turn"], module.Handle("a", new SccHold()).Select(Kind));
        module.Handle("b", new SccRoll());
        Assert.Equal(["held", "beat", "roundEnded", "break", "turn"], module.Handle("b", new SccHold()).Select(Kind));
    }

    [Fact]
    public void Bots_AlwaysProduceLegalMoves_AndGamesFinish()
    {
        var rng = new Random(5);
        for (var trial = 0; trial < 40; trial++)
        {
            var module = new ShipCaptainCrewModule(new RandomDiceRoller(trial), 2);
            for (var i = 0; i < 2 + trial % (ShipCaptainCrewGame.MaxPlayers - 1); i++)
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
    public void Messages_RoundTripOnTheWire()
    {
        var module = new ShipCaptainCrewModule(new ScriptedRoller(6, 5, 4, 2, 2), 3);
        module.AddPlayer("a", "Alice", isBot: false);
        module.AddPlayer("b", "Bob", isBot: false);
        module.Start();

        foreach (var emit in module.Handle("a", new SccRoll()).OfType<ToAll>())
            Assert.IsType(emit.Message.GetType(), NetMessage.Deserialize(emit.Message.Serialize()));

        Assert.IsType<SccHold>(NetMessage.Deserialize(new SccHold().Serialize()));
        Assert.Contains("\"$type\":\"shipcaptaincrew.roll\"", new SccRoll().Serialize());
    }
}
