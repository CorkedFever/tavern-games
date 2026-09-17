using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Blackjack;

public enum BjHandState
{
    /// <summary>No hand this round (hasn't bet yet, or is out of chips).</summary>
    Waiting,
    Playing,
    Stood,
    Doubled,
    Bust,
    Blackjack,
}

public enum BjOutcome
{
    Lose,
    Bust,
    Push,
    Win,
    Blackjack,
}

/// <summary>One seat as everyone sees it. Player cards are always face up in blackjack.</summary>
public sealed record BjSeat(
    string PlayerId,
    int Chips,
    int Bet,
    string[] Cards,
    int Total,
    bool Soft,
    BjHandState State,
    bool Out);

/// <summary>
/// The whole public table. Every blackjack message carries one, taken at the moment of
/// the event, so a client (or a late spectator) only ever has to show the latest table.
/// The dealer's hole card is "??" and left out of <see cref="DealerTotal"/> until revealed.
/// </summary>
public sealed record BjTable(
    int Round,
    int TotalRounds,
    int MinBet,
    bool Betting,
    string? CurrentPlayerId,
    string[] DealerCards,
    int DealerTotal,
    BjSeat[] Seats);

public sealed record BjPayout(string PlayerId, BjOutcome Outcome, int Net);

// ---------- Client -> Server ----------

public sealed record BjBet(int Amount) : NetMessage;
public sealed record BjHit : NetMessage;
public sealed record BjStand : NetMessage;
public sealed record BjDouble : NetMessage;

// ---------- Server -> Client ----------

public sealed record BjBettingOpened(BjTable Table) : NetMessage;
public sealed record BjBetPlaced(string PlayerId, int Amount, BjTable Table) : NetMessage;
public sealed record BjDealt(BjTable Table) : NetMessage;

/// <summary><paramref name="Action"/> is "hit", "stand" or "double"; <paramref name="Card"/> is what was drawn, if anything.</summary>
public sealed record BjPlayed(string PlayerId, string Action, string? Card, BjTable Table) : NetMessage;

/// <summary>The dealer turned over the hole card (<paramref name="Reveal"/>) or drew another.</summary>
public sealed record BjDealerPlayed(bool Reveal, string Card, BjTable Table) : NetMessage;

public sealed record BjRoundSettled(BjPayout[] Payouts, BjTable Table) : NetMessage;

/// <summary>Brings a late spectator up to date.</summary>
public sealed record BjSnapshot(BjTable Table) : NetMessage;
