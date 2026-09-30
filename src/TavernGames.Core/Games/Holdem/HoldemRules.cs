using TavernGames.Core.Platform;

namespace TavernGames.Core.Games.Holdem;

/// <summary>
/// How to play Texas Hold'em, as the plugin shows it on the game's screen and in the Rules popup,
/// and as the site shows it. It describes this engine, not the game as played elsewhere: change
/// it with the rules it describes. Fact-checked against the engine, the module and the client.
/// </summary>
public static class HoldemRules
{
    public static IReadOnlyList<RulesSection> Sections { get; } =
    [
        new("The aim",
            "Everyone starts with the same chips, and running out puts you out. Win pots with the best hand or by making the others fold, and be the last player with chips left."),
        new("The deal",
            "The dealer button, marked D, moves one seat each hand, and the two players on its left post the small and big blind, forced bets that can double every few hands. With two players, the button posts the small one. If you can't cover a blind, you post what you have and are all in."),
        new("Betting",
            "You get two private cards, then five shared ones: three on the flop, one on the turn, one on the river. Betting starts left of the big blind before the flop, and left of the button after each of those. A round ends when everyone still in has had a turn and matched the bet."),
        new("Your turn",
            "Press Fold to give up, Check when there's nothing to call, or Call to match the bet. To bet or raise, set the slider or press Min, Half, Pot or All in, then press Bet or Raise to. A raise must add at least the last full bet or raise that round, and at least the big blind."),
        new("All in",
            "You may go all in for less than a full raise, but anyone who has acted since the last full bet or raise can then only call or fold. When everyone else still in is all in, you can only call or fold. All in, you act no more and only win what you matched from each player: bigger bets make a side pot, and a bet nobody matched goes back. If nobody can bet any more, the hands turn up and the board is dealt out."),
        new("Showdown",
            "If everyone else folds, you take the pot without showing. Otherwise the best five of your seven cards win: straight flush, four of a kind, full house, flush, straight, three of a kind, two pair, pair, then high card. Higher cards break ties, suits never do, and an ace can be low in a five high straight. Equal hands split the pot, and any odd chips go one each to the winners nearest the button's left."),
    ];
}
