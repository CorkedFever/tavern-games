using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.ShipCaptainCrew;

/// <summary>
/// One seat as everyone sees it. The dice are the five as last rolled this round, with
/// <paramref name="Kept"/> marking the ship, the captain and the crew once each is aboard;
/// the unkept two are the cargo. <paramref name="InRound"/> is false for a seat sitting out
/// a roll-off. Dice are public in this game: the whole bar watches the throw.
/// </summary>
public sealed record SccSeat(
    string PlayerId,
    int Points,
    bool Ship,
    bool Captain,
    bool Crew,
    int[] Dice,
    bool[] Kept,
    int RollsLeft,
    int Score,
    bool Done,
    bool InRound);

/// <summary>
/// The whole public table, carried by every message so a client only shows the latest.
/// <paramref name="LeaderScore"/> and <paramref name="LeaderId"/> are the best cargo held so
/// far this round, or null before anyone has held.
/// </summary>
public sealed record SccTable(
    int Round,
    int RoundsToWin,
    string? CurrentPlayerId,
    int? LeaderScore,
    string? LeaderId,
    SccSeat[] Seats);

// ---------- Client -> Server ----------

/// <summary>Throw every die not yet set aside.</summary>
public sealed record SccRoll : NetMessage;

/// <summary>Keep the cargo as it lies and end the turn. Only with the crew aboard.</summary>
public sealed record SccHold : NetMessage;

// ---------- Server -> Client ----------

public sealed record SccTurnStarted(string PlayerId, SccTable Table) : NetMessage;

/// <summary>A throw: the five dice, which are now set aside, and how many rolls are left.</summary>
public sealed record SccRolled(string PlayerId, int[] Dice, bool[] Kept, int RollsLeft, SccTable Table) : NetMessage;

/// <summary>The turn is over, by choice or out of rolls; <paramref name="Score"/> is the cargo, or 0 with no crew.</summary>
public sealed record SccHeld(string PlayerId, int Score, SccTable Table) : NetMessage;

/// <summary>
/// The round is decided: a winner with the best cargo, or a tie that sends <paramref name="Tied"/>
/// into a roll-off, or nobody at all when every seat came up empty and the round is replayed.
/// </summary>
public sealed record SccRoundEnded(string? WinnerId, int Score, bool TieBreak, string[] Tied, SccTable Table) : NetMessage;

/// <summary>Brings a late spectator up to date.</summary>
public sealed record SccSnapshot(SccTable Table) : NetMessage;
