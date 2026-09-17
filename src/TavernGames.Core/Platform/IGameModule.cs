using TavernGames.Core.Protocol;

namespace TavernGames.Core.Platform;

/// <summary>
/// The authoritative rules of one tavern game, as the room sees them. A module owns
/// the game state, validates every move, and answers with the <see cref="Emit"/>s that
/// describe the result. It never touches sockets, timers or threads: the room
/// serializes all calls, so a module can be plain single-threaded code.
///
/// Illegal moves throw <see cref="InvalidOperationException"/> (or ArgumentException);
/// the room reports the message back to the offending player.
/// </summary>
public interface IGameModule
{
    /// <summary>Stable wire id, e.g. "liarsdice".</summary>
    string GameType { get; }

    int MinPlayers { get; }
    int MaxPlayers { get; }

    GamePhase Phase { get; }

    /// <summary>The seat that must act next, or null when nobody is on the clock.</summary>
    string? CurrentActorId { get; }

    /// <summary>Public roster, including the game's own per-seat tally (dice left, score, lives...).</summary>
    PlayerPublic[] Roster();

    void AddPlayer(string id, string name, bool isBot);

    /// <summary>Lobby or mid-game departure. Returns what the table should be told, if anything.</summary>
    IReadOnlyList<Emit> RemovePlayer(string id);

    IReadOnlyList<Emit> Start();

    /// <summary>Applies a game-specific move from <paramref name="playerId"/>.</summary>
    IReadOnlyList<Emit> Handle(string playerId, NetMessage move);

    /// <summary>Chooses a move for a bot seat, or null if it has nothing legal to do right now.</summary>
    NetMessage? DecideBotMove(string botId, Random rng);

    /// <summary>Public messages that bring a late spectator up to the current state.</summary>
    IReadOnlyList<NetMessage> CatchUp();
}
