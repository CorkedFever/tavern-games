using TavernGames.Core.Games.LiarsDice;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Tests;

public class PlatformTests
{
    [Fact]
    public void EveryCatalogMessage_RoundTripsThroughTheWireFormat()
    {
        // One instance of each game's client move is enough to prove its types are registered.
        NetMessage[] samples =
        [
            new CreateRoom("Mina", PigModule.Type, new() { ["targetScore"] = 50 }, 900),
            new PlaceBid(3, 4),
            new Challenge(),
            new PigRoll(),
            new PigHeld("p1", 12, 40),
        ];

        foreach (var sample in samples)
        {
            var back = NetMessage.Deserialize(sample.Serialize());
            Assert.IsType(sample.GetType(), back);
        }
    }

    [Fact]
    public void GameMessages_AreNamespacedPerGame()
    {
        Assert.Contains("\"$type\":\"liarsdice.placeBid\"", new PlaceBid(1, 2).Serialize());
        Assert.Contains("\"$type\":\"pig.roll\"", new PigRoll().Serialize());
    }

    [Fact]
    public void Catalog_HasUniqueGameTypesAndMessageNames()
    {
        Assert.Equal(GameCatalog.Games.Count, GameCatalog.Games.Select(g => g.Type).Distinct().Count());

        var names = GameCatalog.Games.SelectMany(g => g.Messages).Select(m => m.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void EveryGame_ExplainsItself_AndEverySetting()
    {
        foreach (var game in GameCatalog.Games)
        {
            Assert.True(game.Rules.Count >= 3, $"{game.Type} needs at least three rules sections");
            Assert.Equal("The aim", game.Rules[0].Heading);
            Assert.Equal(game.Rules.Count, game.Rules.Select(r => r.Heading).Distinct().Count());

            foreach (var section in game.Rules)
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Heading), $"{game.Type} has a section with no heading");
                Assert.False(string.IsNullOrWhiteSpace(section.Text), $"{game.Type}: '{section.Heading}' is empty");
                Assert.True(section.Heading.Split(' ').Length <= 3, $"{game.Type}: '{section.Heading}' is too long for a heading");
                // Drawn as plain wrapped text: no markdown, and no characters the game's font lacks.
                Assert.DoesNotContain("*", section.Text);
                Assert.DoesNotContain("`", section.Text);
                Assert.DoesNotContain("—", section.Text); // an em dash
            }

            foreach (var option in game.Options)
                Assert.False(string.IsNullOrWhiteSpace(option.Help), $"{game.Type}.{option.Key} has no help text");
        }
    }

    [Fact]
    public void Options_FallBackToDefaults_AndClampToRange()
    {
        var pig = GameCatalog.Find("pig")!;

        Assert.Equal(100, pig.ResolveOptions(null)["targetScore"]);
        Assert.Equal(200, pig.ResolveOptions(new Dictionary<string, int> { ["targetScore"] = 9999 })["targetScore"]);
        Assert.Equal(20, pig.ResolveOptions(new Dictionary<string, int> { ["targetScore"] = -5 })["targetScore"]);
    }

    [Fact]
    public void UnknownGame_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() => GameCatalog.Create("chess", null));

    [Fact]
    public void LiarsDiceModule_DealsPrivateHands_ThenAnnouncesTheRound()
    {
        var module = new LiarsDiceModule(new ScriptedRoller(3), startingDice: 2);
        module.AddPlayer("a", "Alice", isBot: false);
        module.AddPlayer("b", "Bot", isBot: true);

        var emits = module.Start();

        // Two private hands first, then one public round message.
        Assert.Equal(2, emits.OfType<ToPlayer>().Count(e => e.Message is YourHand));
        Assert.IsType<RoundStarted>(Assert.IsType<ToAll>(emits[^1]).Message);
        Assert.True(module.Roster().Single(p => p.Id == "b").IsBot);
    }

    [Fact]
    public void LiarsDiceModule_PausesBetweenTheRevealAndTheNextRound()
    {
        var module = new LiarsDiceModule(new ScriptedRoller(3), startingDice: 2);
        module.AddPlayer("a", "Alice", isBot: false);
        module.AddPlayer("b", "Bob", isBot: false);
        module.Start();
        module.Handle("a", new PlaceBid(1, 6)); // a lie: every die is a 3

        var emits = module.Handle("b", new Challenge());

        var kinds = emits.Select(e => e switch
        {
            ToAll { Message: ChallengeResolved } => "resolved",
            Pause => "pause",
            ToPlayer => "hand",
            ToAll { Message: RoundStarted } => "round",
            _ => "other",
        }).ToList();
        Assert.Equal(["resolved", "pause", "hand", "hand", "round"], kinds);
    }

    [Fact]
    public void Modules_RejectMovesFromOtherGames()
    {
        var pig = new PigModule(new ScriptedRoller(3), 100);
        pig.AddPlayer("a", "Alice", false);
        pig.AddPlayer("b", "Bob", false);
        pig.Start();

        Assert.Throws<InvalidOperationException>(() => pig.Handle("a", new PlaceBid(1, 2)));
    }
}
