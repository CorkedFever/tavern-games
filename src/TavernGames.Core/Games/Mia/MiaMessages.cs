using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Mia;

/// <summary>What the seat on the clock owes the table.</summary>
public enum MiaStep
{
    /// <summary>Nobody is on the clock: a call or a concession has just settled the round.</summary>
    None,

    /// <summary>The seat on the clock has rolled under the cup and must announce a value.</summary>
    Announce,

    /// <summary>The seat on the clock must believe the announcement, call liar, or concede to a Mia.</summary>
    Respond,
}

/// <summary>One seat as everyone sees it. Lives are the only per-seat state Mia has.</summary>
public sealed record MiaSeat(string PlayerId, int Lives, bool Out);

/// <summary>
/// The whole public table. Every Mia message carries one, taken at the moment of the
/// event, so a client (or a late spectator) only ever has to show the latest table.
/// The dice under the cup are deliberately not here: they reach their owner alone, in a
/// <see cref="MiaYourRoll"/>, and the table only ever learns them through a call.
/// </summary>
public sealed record MiaTable(
    int StartingLives,
    MiaStep Step,
    string? CurrentPlayerId,
    string? AnnouncerId,
    int Announced,
    int Accepted,
    MiaSeat[] Seats);

// ---------- Client -> Server ----------

/// <summary><paramref name="Value"/> is a two-digit code, e.g. 52, 33 or 21 for Mia.</summary>
public sealed record MiaAnnounce(int Value) : NetMessage;

public sealed record MiaBelieve : NetMessage;
public sealed record MiaCallLiar : NetMessage;

/// <summary>Give up a life rather than call a Mia. Legal only when facing one.</summary>
public sealed record MiaConcede : NetMessage;

// ---------- Server -> Client ----------

/// <summary>A fresh round: <paramref name="PlayerId"/> takes the cup with nothing to beat.</summary>
public sealed record MiaRoundStarted(string PlayerId, MiaTable Table) : NetMessage;

/// <summary>The cup was shaken. The table learns who rolled, never what they rolled.</summary>
public sealed record MiaRolled(string PlayerId, MiaTable Table) : NetMessage;

/// <summary>Private: the dice under this connection's cup, as a two-digit code.</summary>
public sealed record MiaYourRoll(int Value) : NetMessage;

public sealed record MiaAnnounced(string PlayerId, int Value, MiaTable Table) : NetMessage;

/// <summary><paramref name="Value"/> is the announcement that was taken at its word; the believer now has to beat it.</summary>
public sealed record MiaBelieved(string PlayerId, int Value, MiaTable Table) : NetMessage;

/// <summary>
/// A call, with the dice it exposed. <paramref name="Honest"/> means the roll reached the
/// announcement, which costs the caller instead of the announcer. A Mia costs two lives
/// either way, so <paramref name="LivesLost"/> carries the price.
/// </summary>
public sealed record MiaCalled(
    string CallerId,
    string AnnouncerId,
    int Announced,
    int Revealed,
    bool Honest,
    string LoserId,
    int LivesLost,
    bool LoserEliminated,
    MiaTable Table) : NetMessage;

/// <summary>Someone gave up a life rather than call a Mia. The dice stay under the cup.</summary>
public sealed record MiaConceded(
    string PlayerId,
    string AnnouncerId,
    int Announced,
    bool Eliminated,
    MiaTable Table) : NetMessage;

/// <summary>Brings a late spectator up to date.</summary>
public sealed record MiaSnapshot(MiaTable Table) : NetMessage;
