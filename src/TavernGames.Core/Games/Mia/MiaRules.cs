using TavernGames.Core.Platform;

namespace TavernGames.Core.Games.Mia;

/// <summary>
/// How to play Mia, as the plugin shows it on the game's screen and in the Rules popup,
/// and as the site shows it. It describes this engine, not the game as played elsewhere: change
/// it with the rules it describes. Fact-checked against the engine, the module and the client.
/// </summary>
public static class MiaRules
{
    public static IReadOnlyList<RulesSection> Sections { get; } =
    [
        new("The aim",
            "Everyone starts with equal lives. Lose them all and you're out. The last player with lives left wins."),
        new("Reading the dice",
            "Two dice read as one number, higher die first, so a 5 and a 2 is 52. Mixed rolls run 31 up to 65, then doubles 11 up to 66. A 2 and a 1 is Mia, and nothing beats it."),
        new("Your turn",
            "Your dice roll automatically under your cup, and only you see them. Press the Truth key to announce them, or any value in the list to bluff. A round opens with any announcement, and later ones must beat the last value believed, even if that means bluffing. This table doesn't let you pass the cup on unseen."),
        new("Answering",
            "The next player answers. Press Believe it to take the cup, roll fresh and announce higher. Press Call liar! to lift the cup. If the dice match or beat the claim, the caller loses a life, and if not, the announcer does."),
        new("Facing Mia",
            "You can't believe a Mia. If you call it, whoever was wrong loses two lives. Press Concede a life to lose just one, and the dice stay hidden."),
        new("New rounds",
            "Every lost life ends the round. The loser starts the next with nothing to beat, or the player after them if they're out. If the cup holder or the answering player leaves, the round restarts and nobody loses a life."),
    ];
}
