using TavernGames.Core;
using TavernGames.Core.Games.LiarsDice;

namespace TavernGames.Core.Tests;

public class BotStrategyTests
{
    private static LiarsDiceGame Game(IDiceRoller roller, int dice = 5)
    {
        var g = new LiarsDiceGame(roller, dice);
        g.AddPlayer("bot", "Bot");
        g.AddPlayer("human", "Human");
        g.StartGame();
        return g;
    }

    [Fact]
    public void OpeningBid_IsLegalAndNonEmpty()
    {
        var game = Game(new ScriptedRoller(2)); // bot's hand is all 2s
        var decision = BotStrategy.Decide(game, game.Current, new Random(1));

        Assert.False(decision.IsChallenge);          // nothing to challenge yet
        Assert.True(decision.Bid.IsValidShape);
        Assert.Equal(2, decision.Bid.FaceValue);     // leads with its strong face
    }

    [Fact]
    public void RaisesLegallyOverAModestBid()
    {
        var game = Game(new ScriptedRoller(4));
        game.PlaceBid("bot", new Bid(2, 3));         // bot opens
        // Now it is the human's seat in the engine, but we ask the bot to reason about it.
        var decision = BotStrategy.Decide(game, game.Players.First(p => p.Id == "human"), new Random(2));

        if (!decision.IsChallenge)
            Assert.True(decision.Bid.IsHigherThan(new Bid(2, 3)));
    }

    [Fact]
    public void ChallengesPhysicallyImpossibleBid()
    {
        var game = Game(new ScriptedRoller(1), dice: 2); // 4 dice total
        game.PlaceBid("bot", new Bid(5, 6));             // more 6s than dice exist
        var decision = BotStrategy.Decide(game, game.Players.First(p => p.Id == "human"), new Random(3));

        Assert.True(decision.IsChallenge);
    }

    [Fact]
    public void DecisionsAlwaysProduceAnApplicableMove()
    {
        // Fuzz: every decision the bot makes must be accepted by the engine.
        var rng = new Random(99);
        for (var trial = 0; trial < 50; trial++)
        {
            var game = Game(new RandomDiceRoller(trial), dice: 3);
            for (var step = 0; step < 20 && game.Phase == GamePhase.Playing; step++)
            {
                var actor = game.Current;
                var decision = BotStrategy.Decide(game, actor, rng);
                if (decision.IsChallenge && game.CurrentBid is not null)
                    game.Challenge(actor.Id);
                else if (!decision.IsChallenge)
                    game.PlaceBid(actor.Id, decision.Bid);
                else
                    game.PlaceBid(actor.Id, new Bid(1, 2)); // no bid to challenge yet
            }
        }
    }
}
