using TavernGames.Core.Platform;

namespace TavernGames.Core.Games.Roulette;

/// <summary>
/// How to play Roulette, as the plugin shows it on the game's screen and in the Rules popup,
/// and as the site shows it. It describes this engine, not the game as played elsewhere: change
/// it with the rules it describes. Fact-checked against the engine, the module and the client.
/// </summary>
public static class RouletteRules
{
    public static IReadOnlyList<RulesSection> Sections { get; } =
    [
        new("The aim",
            "You bet against the house, not each other, and everyone plays the same wheel. Whoever has the most chips after the last spin wins."),
        new("Placing bets",
            "Everyone bets at once. Pick a chip worth one, two, five, ten or twenty times the minimum bet, then click a spot on the felt to bet it. Click again to add more, or press Clear bets to take it all back. This table doesn't take splits, streets or corners."),
        new("Payouts",
            "A single number, 0 included, pays 35 to 1. A dozen pays 2 to 1, and so does a column, the 2:1 spot that bets on its whole row. Red, black, odd, even, 1-18 and 19-36 pay 1 to 1. When the ball lands on 0, only a bet on 0 wins."),
        new("The spin",
            "Press Done betting to lock your bets in. To skip a spin, leave the felt empty and press Sit this one out. The wheel turns once everyone still in the game is done. Then winning bets are paid, their stakes come back too, and the next spin opens on an empty felt."),
        new("Winning",
            "Before each spin, anyone with fewer chips than the minimum bet is out for the rest of the game. The game ends after the last spin, or sooner if everyone is out. Most chips wins, and a tie goes to the earlier seat."),
    ];
}
