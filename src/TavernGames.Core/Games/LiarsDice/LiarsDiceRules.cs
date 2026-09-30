using TavernGames.Core.Platform;

namespace TavernGames.Core.Games.LiarsDice;

/// <summary>
/// How to play Liar's Dice, as the plugin shows it on the game's screen and in the Rules popup,
/// and as the site shows it. It describes this engine, not the game as played elsewhere: change
/// it with the rules it describes. Fact-checked against the engine, the module and the client.
/// </summary>
public static class LiarsDiceRules
{
    public static IReadOnlyList<RulesSection> Sections { get; } =
    [
        new("The aim",
            "Be the last player with dice left. Every call you lose costs you a die."),
        new("The roll",
            "Everyone starts with the same number of dice. Each round, all dice are rolled in secret and you see only yours."),
        new("Bidding",
            "A bid says the whole table holds at least so many of one face, like 4 fives. Everyone's dice count, yours too. Ones are not wild here, so each die counts only as its own face."),
        new("Your turn",
            "Turns go round the table, and on yours you bid or call liar. The opening bid can be any count and face, as there is nothing to call yet. Each later bid needs more dice of any face, or the same count of a higher face. Set the count with - and +, press the die with your face, then the key that reads your bid, like Bid 4 fives."),
        new("Calling liar",
            "Anyone still in except the bidder can press Call liar! at any time, even out of turn. Every cup comes off and that face is counted. If there are at least as many as bid, the caller loses a die. If fewer, the bidder loses one."),
        new("Winning",
            "After a call, everyone rolls again and the loser opens the next round. Lose your last die and you are out, so the next player still in opens instead. The last player with dice wins."),
    ];
}
