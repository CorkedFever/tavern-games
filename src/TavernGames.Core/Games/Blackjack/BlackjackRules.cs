using TavernGames.Core.Platform;

namespace TavernGames.Core.Games.Blackjack;

/// <summary>
/// How to play Blackjack, as the plugin shows it on the game's screen and in the Rules popup,
/// and as the site shows it. It describes this engine, not the game as played elsewhere: change
/// it with the rules it describes. Fact-checked against the engine, the module and the client.
/// </summary>
public static class BlackjackRules
{
    public static IReadOnlyList<RulesSection> Sections { get; } =
    [
        new("The aim",
            "Beat the dealer without going over 21, and finish with the most chips."),
        new("Card values",
            "Number cards count face value, and face cards count 10. An ace counts 11, or 1 if 11 would put you over 21. A soft total has an ace counting 11. An ace and a 10-value card as your first two cards is a blackjack."),
        new("Betting",
            "Everyone bets at once. Set an amount with the slider or the Min, Half and All in buttons, then press Bet. Then everyone gets two cards face up, and the dealer one up and one down."),
        new("Your turn",
            "Players go in seat order. Press Hit for a card or Stand to stop, but over 21 is a bust and loses your bet. On your first two cards, press Double to double your bet and take exactly one more card. This table has no splitting or insurance."),
        new("The dealer",
            "The dealer then flips the face-down card and draws to 17 or more. Beat the dealer's total, or stay in while the dealer busts, for even money. A tie is a push and returns your bet, and a blackjack pays 3 to 2, rounded down. A dealer blackjack ends the round at once, beating all but another blackjack, which ties."),
        new("Winning",
            "The game ends after the last round, or once everyone is out. You're out when you can't cover the minimum bet. A tie for most chips goes to the earlier seat."),
    ];
}
