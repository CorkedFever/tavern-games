using TavernGames.Core.Protocol;

namespace TavernGames.Core.Platform;

/// <summary>
/// One step of output from a game module. The room plays a module's emits back in
/// order, so a module describes *what* everyone should see and the room owns *how*
/// it gets there (sockets, spectators, pacing).
/// </summary>
public abstract record Emit;

/// <summary>Send to every player and spectator.</summary>
public sealed record ToAll(NetMessage Message) : Emit;

/// <summary>Send privately to one seat (secret hands). Ignored for bots, which have no socket.</summary>
public sealed record ToPlayer(string PlayerId, NetMessage Message) : Emit;

public enum PauseKind
{
    /// <summary>A short breath, e.g. after a bust, so the moment registers.</summary>
    Beat,

    /// <summary>A longer break between rounds so players can read a reveal.</summary>
    RoundBreak
}

/// <summary>Hold the table for a moment before the next emit. Skipped when pacing is zero (tests).</summary>
public sealed record Pause(PauseKind Kind) : Emit;
