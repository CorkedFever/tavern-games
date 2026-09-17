using TavernGames.Core.Cards;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Holdem;

/// <summary>How far a hand has got. The board holds 0, 3, 4 and 5 cards on the first four.</summary>
public enum HoldemStreet
{
    Preflop,
    Flop,
    Turn,
    River,
    Showdown,
}

/// <summary>
/// The four things a seat can do. Going all-in is not a move of its own: it is a
/// <see cref="Call"/> that takes the last chip, or a <see cref="Raise"/> to the whole stack.
/// </summary>
public enum HoldemMove
{
    Fold,
    Check,
    Call,
    Raise,
}

public enum HoldemAwardKind
{
    /// <summary>The main pot, contested by everyone who was still in at the end.</summary>
    Main,

    /// <summary>A side pot, contested only by the seats that covered it.</summary>
    Side,

    /// <summary>Chips no opponent could match, handed straight back. Not a win.</summary>
    Uncalled,
}

/// <summary>
/// One seat as everyone is allowed to see it. <see cref="Cards"/> is empty when the seat
/// holds nothing (not dealt in, or folded), two <see cref="Card.HiddenCode"/> backs while
/// the hand is live, and the real codes only once the seat has shown them at a showdown.
/// </summary>
public sealed record HoldemSeat(
    string PlayerId,
    int Chips,
    int StreetBet,
    int Committed,
    string[] Cards,
    bool Folded,
    bool AllIn,
    bool Eliminated);

/// <summary>
/// A pot as it stands right now, and the seats that can still win it. Mid-street the bets
/// are uneven by construction, so the breakdown only describes real side pots once a seat is
/// all-in: see <see cref="HoldemTable.Pots"/>.
/// </summary>
public sealed record HoldemPotView(int Amount, string[] Eligible);

/// <summary>
/// The whole public table. Every hold'em message carries one, taken at the moment of the
/// event, so a client (or a late spectator) only ever has to render the latest table.
/// <see cref="ToCall"/>, <see cref="MinRaiseTo"/>, <see cref="MaxRaiseTo"/> and
/// <see cref="CanRaise"/> describe the seat on the clock, which is public knowledge, so a
/// client can draw legal controls without knowing anything private.
///
/// <see cref="Pot"/> is always the whole pot. <see cref="Pots"/> splits it the way it would
/// be paid out if the hand ended here, which while a street is still being bet says nothing
/// useful: an unmatched blind or an unanswered bet reads as a side pot only its owner can
/// win. Draw the split only once some seat is all-in; until then show the total.
/// </summary>
public sealed record HoldemTable(
    int Hand,
    int SmallBlind,
    int BigBlind,
    int BlindLevel,
    int HandsUntilBlindsUp,
    HoldemStreet Street,
    string[] Community,
    int Pot,
    HoldemPotView[] Pots,
    string? ButtonId,
    string? CurrentPlayerId,
    int CurrentBet,
    int ToCall,
    int MinRaiseTo,
    int MaxRaiseTo,
    bool CanRaise,
    HoldemSeat[] Seats);

/// <summary>One hand turned face up. <paramref name="Hand"/> is filled in only once the board is complete.</summary>
public sealed record HoldemReveal(string PlayerId, string[] Cards, string? Hand);

/// <summary>Chips moving to a seat at the end of a hand. <paramref name="Hand"/> is set when the hand was shown.</summary>
public sealed record HoldemAward(string PlayerId, int Amount, HoldemAwardKind Kind, string? Hand);

// ---------- Client -> Server ----------

/// <summary>
/// One action from the seat on the clock. <paramref name="Amount"/> is the raise-TO total
/// for this street and is ignored by every other move; an all-in raise is simply a raise to
/// the seat's whole stack.
/// </summary>
public sealed record HoldemAct(HoldemMove Move, int Amount = 0) : NetMessage;

// ---------- Server -> Client ----------

/// <summary>A new hand: the button has moved, the blinds are in and the cards are out.</summary>
public sealed record HoldemHandStarted(HoldemTable Table) : NetMessage;

/// <summary>Your two hole cards, and nobody else's. Sent privately, once per hand.</summary>
public sealed record HoldemYourCards(string[] Cards) : NetMessage;

/// <summary>
/// A blind going in. Both blinds are posted as the hand is dealt, so the table here is
/// already the state after both of them: the message says who paid what, not what the table
/// looked like halfway through paying.
/// </summary>
public sealed record HoldemBlindPosted(string PlayerId, int Amount, bool Big, HoldemTable Table) : NetMessage;

/// <summary><paramref name="Amount"/> is the chips paid for a call, or the raise-TO total for a raise.</summary>
public sealed record HoldemActed(string PlayerId, HoldemMove Move, int Amount, HoldemTable Table) : NetMessage;

public sealed record HoldemStreetDealt(HoldemStreet Street, string[] Dealt, HoldemTable Table) : NetMessage;

/// <summary>
/// Hands are face up. Sent once when the cards are turned over, and again with the hand
/// descriptions once the board is complete, which is how an all-in run-out plays at a real table.
/// </summary>
public sealed record HoldemShowdown(HoldemReveal[] Reveals, HoldemTable Table) : NetMessage;

public sealed record HoldemHandEnded(HoldemAward[] Awards, string[] BustedOut, HoldemTable Table) : NetMessage;

/// <summary>Brings a late spectator up to date.</summary>
public sealed record HoldemSnapshot(HoldemTable Table) : NetMessage;
