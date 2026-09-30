using TavernGames.Core.Platform;

namespace TavernGames.Core.Games.ShipCaptainCrew;

/// <summary>
/// How to play Ship, Captain & Crew, as the plugin shows it on the game's screen and in the Rules popup,
/// and as the site shows it. It describes this engine, not the game as played elsewhere: change
/// it with the rules it describes. Fact-checked against the engine, the module and the client.
/// </summary>
public static class ShipCaptainCrewRules
{
    public static IReadOnlyList<RulesSection> Sections { get; } =
    [
        new("The aim",
            "Get a ship, a captain and a crew aboard, and your other two dice become your cargo. The highest cargo wins the round, and enough rounds win you the game."),
        new("Your turn",
            "Players take turns in seat order, each with five dice and three rolls. Press Roll to throw all five, then press Roll again to throw every die not set aside. Your third roll always ends your turn."),
        new("Ship, captain, crew",
            "A 6 is the ship, a 5 the captain and a 4 the crew. They must come aboard in that order, and the game sets each aside as it lands. All three can come in one throw, but a 5 or 4 that comes too early is thrown again."),
        new("The cargo",
            "Once the crew is aboard, your other two dice add up to your cargo. Press Hold to keep it and end your turn, or press Throw the cargo to throw both again and take whatever they show. This table doesn't let you keep one cargo die and throw the other. No crew after three rolls scores nothing."),
        new("Winning",
            "When everyone has thrown, the highest cargo wins the round. If the top cargo is tied, only those players throw again in a roll-off. If nobody gets a crew aboard, even in a roll-off, everyone throws the round again. First to the set number of rounds wins the game."),
    ];
}
