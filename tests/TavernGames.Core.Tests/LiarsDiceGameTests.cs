using TavernGames.Core;
using TavernGames.Core.Games.LiarsDice;

namespace TavernGames.Core.Tests;

public class LiarsDiceGameTests
{
    private static LiarsDiceGame TwoPlayerGame(IDiceRoller roller, int dice = 5)
    {
        var game = new LiarsDiceGame(roller, dice);
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.StartGame();
        return game;
    }

    [Fact]
    public void StartGame_DealsDiceAndEntersBidding()
    {
        var game = TwoPlayerGame(new ScriptedRoller(3));

        Assert.Equal(GamePhase.Playing, game.Phase);
        Assert.All(game.Players, p => Assert.Equal(5, p.Dice.Count));
        Assert.Equal("a", game.Current.Id);
    }

    [Fact]
    public void StartGame_RequiresTwoPlayers()
    {
        var game = new LiarsDiceGame(new ScriptedRoller(1));
        game.AddPlayer("a", "Alice");
        Assert.Throws<InvalidOperationException>(() => game.StartGame());
    }

    [Fact]
    public void PlaceBid_RejectsNonRaise()
    {
        var game = TwoPlayerGame(new ScriptedRoller(3));
        game.PlaceBid("a", new Bid(2, 4));
        Assert.Throws<InvalidOperationException>(() => game.PlaceBid("b", new Bid(2, 4)));
        Assert.Throws<InvalidOperationException>(() => game.PlaceBid("b", new Bid(1, 6)));
    }

    [Fact]
    public void PlaceBid_RejectsOutOfTurn()
    {
        var game = TwoPlayerGame(new ScriptedRoller(3));
        Assert.Throws<InvalidOperationException>(() => game.PlaceBid("b", new Bid(1, 2)));
    }

    [Fact]
    public void Challenge_WrongCaller_LosesADie()
    {
        // Every die is a 3 -> ten 3s on the table. Bidding "four 3s" is clearly true.
        var game = TwoPlayerGame(new ScriptedRoller(3));
        game.PlaceBid("a", new Bid(4, 3));

        var outcome = game.Challenge("b");

        Assert.True(outcome.BidWasValid);
        Assert.Equal(10, outcome.ActualCount);
        Assert.Equal("b", outcome.LoserId);
        Assert.Equal(4, game.Players.First(p => p.Id == "b").DiceCount);
        Assert.Equal(5, game.Players.First(p => p.Id == "a").DiceCount);
    }

    [Fact]
    public void Challenge_CaughtBidder_LosesADie()
    {
        // No die shows a 6, so "one 6" is a lie and the bidder loses.
        var game = TwoPlayerGame(new ScriptedRoller(3));
        game.PlaceBid("a", new Bid(1, 6));

        var outcome = game.Challenge("b");

        Assert.False(outcome.BidWasValid);
        Assert.Equal(0, outcome.ActualCount);
        Assert.Equal("a", outcome.LoserId);
        Assert.Equal(4, game.Players.First(p => p.Id == "a").DiceCount);
    }

    [Fact]
    public void Game_EndsWhenOnePlayerRemains()
    {
        // One die each so a single lost challenge eliminates a player.
        var game = TwoPlayerGame(new ScriptedRoller(3), dice: 1);
        game.PlaceBid("a", new Bid(1, 6)); // lie: no 6s
        var outcome = game.Challenge("b");

        Assert.True(outcome.GameOver);
        Assert.Equal("b", outcome.WinnerId);
        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.True(game.Players.First(p => p.Id == "a").IsEliminated);
    }

    [Fact]
    public void Challenge_MayComeFromAnyPlayer_NotJustCurrent()
    {
        var game = new LiarsDiceGame(new ScriptedRoller(3));
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.AddPlayer("c", "Carol");
        game.StartGame();

        game.PlaceBid("a", new Bid(1, 6)); // a lies (no 6s); turn passes to b
        Assert.Equal("b", game.Current.Id);

        // c calls liar even though it's b's turn — open calling.
        var outcome = game.Challenge("c");

        Assert.Equal("c", outcome.ChallengerId);
        Assert.False(outcome.BidWasValid);
        Assert.Equal("a", outcome.LoserId); // the caught bidder loses
    }

    [Fact]
    public void Challenge_CannotCallYourOwnBid()
    {
        var game = TwoPlayerGame(new ScriptedRoller(3));
        game.PlaceBid("a", new Bid(1, 6));
        Assert.Throws<InvalidOperationException>(() => game.Challenge("a"));
    }

    [Fact]
    public void Challenge_RevealsAllHands()
    {
        var game = TwoPlayerGame(new ScriptedRoller(3));
        game.PlaceBid("a", new Bid(1, 2));
        var outcome = game.Challenge("b");

        Assert.Equal(2, outcome.Reveal_Count());
    }
}

internal static class OutcomeExtensions
{
    public static int Reveal_Count(this ChallengeOutcome o) => o.RevealedHands.Count;
}
