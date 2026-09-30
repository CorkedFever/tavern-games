using TavernGames.Core.Platform;

namespace TavernGames.Core.Games.Pig;

/// <summary>
/// How to play Pig, as the plugin shows it on the game's screen and in the Rules popup,
/// and as the site shows it. It describes this engine, not the game as played elsewhere: change
/// it with the rules it describes. Fact-checked against the engine, the module and the client.
/// </summary>
public static class PigRules
{
    public static IReadOnlyList<RulesSection> Sections { get; } =
    [
        new("The aim",
            "Be the first to bank enough points to reach the target score. The target is 100 unless the host picked another."),
        new("Your turn",
            "Play goes around the table in seat order, starting with the first seat. On your turn, press Roll to throw one die. A 2 to 6 adds that many points to the total riding on your turn. You can keep rolling as long as you dare."),
        new("Rolling a one",
            "Roll a 1 and you bust. Everything riding this turn is lost and the die passes to the next player. Points you banked on earlier turns are safe."),
        new("Holding",
            "Press Hold to add what is riding to your score and pass the die. The key shows the amount, like Hold 12. You have to roll at least once before you can hold."),
        new("Winning",
            "You win the moment you hold and your score reaches or passes the target. The game ends right there, so nobody gets a last turn to catch up. Points riding do not count until you hold. If everyone else leaves, the last player left wins."),
    ];
}
