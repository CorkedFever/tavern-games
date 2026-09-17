using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Game;

/// <summary>
/// The plugin-side half of a tavern game: it tracks that game's messages, words its
/// log and chat narration, and draws its table. The shell (<see cref="GameSession"/>
/// and the main window) handles everything games share: connecting, rooms, rosters,
/// bots, spectating. Adding a game to the plugin means adding one of these.
/// </summary>
public interface IClientGame
{
    /// <summary>Matches the server module's wire id, e.g. "liarsdice".</summary>
    string GameType { get; }

    /// <summary>The seat on the clock, for the roster's turn marker. Empty when unknown.</summary>
    string CurrentPlayerId { get; }

    void Reset();

    /// <summary>Applies one of this game's messages. Returns false if the message isn't this game's.</summary>
    bool Apply(NetMessage message, GameSession session);

    /// <summary>An in-character chat line for the message, or null to stay quiet.</summary>
    string? Narrate(NetMessage message, GameSession session);

    /// <summary>Draws the table while the game is being played.</summary>
    void DrawTable(GameSession session, Action<NetMessage> send);

    /// <summary>Draws one seat's tally at the end of its roster row (hidden dice, a score bar...).</summary>
    void DrawSeatTally(PlayerPublic seat);
}
