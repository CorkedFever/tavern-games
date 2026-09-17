using TavernGames.Core;
using TavernGames.Core.Games.Pig;

namespace TavernGames.Core.Tests;

public class PigGameTests
{
    private static PigGame Game(IDiceRoller roller, int target = 100)
    {
        var game = new PigGame(roller, target);
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.StartGame();
        return game;
    }

    [Fact]
    public void Rolling_AddsToTurnTotal_AndKeepsTheTurn()
    {
        var game = Game(new ScriptedRoller(4, 6));

        var first = game.Roll("a");
        var second = game.Roll("a");

        Assert.False(first.Busted);
        Assert.Equal(10, second.TurnTotal);
        Assert.Equal("a", game.Current.Id);
        Assert.Equal(0, game.Players[0].Score); // nothing is banked until you hold
    }

    [Fact]
    public void RollingAOne_LosesTheTurnTotal_AndPassesPlay()
    {
        var game = Game(new ScriptedRoller(5, 1));
        game.Roll("a");

        var bust = game.Roll("a");

        Assert.True(bust.Busted);
        Assert.Equal(0, game.TurnTotal);
        Assert.Equal(0, game.Players[0].Score);
        Assert.Equal("b", game.Current.Id);
    }

    [Fact]
    public void Holding_BanksTheTurnTotal_AndPassesPlay()
    {
        var game = Game(new ScriptedRoller(6));
        game.Roll("a");
        game.Roll("a");

        var held = game.Hold("a");

        Assert.Equal(12, held.Banked);
        Assert.Equal(12, game.Players[0].Score);
        Assert.Equal("b", game.Current.Id);
    }

    [Fact]
    public void Hold_RequiresAtLeastOneRoll()
    {
        var game = Game(new ScriptedRoller(6));
        Assert.Throws<InvalidOperationException>(() => game.Hold("a"));
    }

    [Fact]
    public void Moves_AreRejectedOutOfTurn()
    {
        var game = Game(new ScriptedRoller(6));
        Assert.Throws<InvalidOperationException>(() => game.Roll("b"));
    }

    [Fact]
    public void ReachingTheTarget_OnHold_WinsTheGame()
    {
        var game = Game(new ScriptedRoller(6), target: 12);
        game.Roll("a");
        game.Roll("a");

        var held = game.Hold("a");

        Assert.True(held.Won);
        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal("a", game.WinnerId);
    }

    [Fact]
    public void CurrentPlayerLeaving_GivesTheNextSeatAFreshTurn()
    {
        var game = new PigGame(new ScriptedRoller(5));
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.AddPlayer("c", "Carol");
        game.StartGame();
        game.Roll("a"); // a has 5 riding

        game.RemovePlayer("a");

        Assert.Equal("b", game.Current.Id);
        Assert.Equal(0, game.TurnTotal);
        Assert.Equal(GamePhase.Playing, game.Phase);
    }

    [Fact]
    public void LastPlayerStanding_WinsWhenOthersLeave()
    {
        var game = Game(new ScriptedRoller(5));
        game.RemovePlayer("a");

        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal("b", game.WinnerId);
    }

    [Fact]
    public void Bot_AlwaysProducesLegalMoves_AndGamesFinish()
    {
        var rng = new Random(7);
        for (var trial = 0; trial < 50; trial++)
        {
            var game = Game(new RandomDiceRoller(trial), target: 50);
            for (var step = 0; step < 2000 && game.Phase == GamePhase.Playing; step++)
            {
                var actor = game.Current;
                if (PigBot.ShouldRoll(game, actor, rng)) game.Roll(actor.Id);
                else game.Hold(actor.Id);
            }
            Assert.Equal(GamePhase.GameOver, game.Phase);
        }
    }

    [Fact]
    public void Bot_BanksTheWin_RatherThanRiskingIt()
    {
        var game = Game(new ScriptedRoller(6), target: 12);
        game.Roll("a");
        game.Roll("a"); // 12 riding, exactly the target

        Assert.False(PigBot.ShouldRoll(game, game.Current, new Random(1)));
    }
}
