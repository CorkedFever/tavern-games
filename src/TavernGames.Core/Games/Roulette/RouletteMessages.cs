using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Roulette;

/// <summary>
/// The bets the layout takes. A straight is one number; the rest are the outside bets.
/// Splits, streets and corners are not offered: they need clicks on the lines between
/// cells, and the table is played with a mouse in a game window.
/// </summary>
public enum RouletteBetKind
{
    Straight,
    Red,
    Black,
    Odd,
    Even,
    Low,
    High,
    Dozen,
    Column,
}

/// <summary>
/// Chips on one spot of the layout. <paramref name="Pick"/> is the number for a straight
/// and 1 to 3 for a dozen or a column; the other kinds ignore it.
/// </summary>
public sealed record RouletteBet(RouletteBetKind Kind, int Pick, int Amount);

/// <summary>One seat as everyone sees it. Bets are public: the chips are on the table.</summary>
public sealed record RouletteSeat(string PlayerId, int Chips, RouletteBet[] Bets, bool Done, bool Out);

/// <summary>
/// The whole public table. Every roulette message carries one, taken at the moment of the
/// event, so a client only ever has to show the latest. <paramref name="History"/> is the
/// last few numbers, newest first, the way the board over a real wheel shows them.
/// </summary>
public sealed record RouletteTable(
    int Round,
    int TotalRounds,
    int MinBet,
    bool Betting,
    int? LastNumber,
    int[] History,
    RouletteSeat[] Seats);

/// <summary>What a seat put on the table this spin and what came back, stakes included.</summary>
public sealed record RoulettePayout(string PlayerId, int Staked, int Returned);

// ---------- Client -> Server ----------

/// <summary>Puts <paramref name="Amount"/> chips on a spot; more on the same spot adds to it.</summary>
public sealed record RoulettePlace(RouletteBetKind Kind, int Pick, int Amount) : NetMessage;

/// <summary>Takes every chip you put down this spin back.</summary>
public sealed record RouletteClear : NetMessage;

/// <summary>Your bets are down. With none, you sit the spin out. The wheel turns once everyone has said so.</summary>
public sealed record RouletteDone : NetMessage;

// ---------- Server -> Client ----------

public sealed record RouletteBettingOpened(RouletteTable Table) : NetMessage;

public sealed record RouletteBetPlaced(string PlayerId, RouletteBet Bet, RouletteTable Table) : NetMessage;

public sealed record RouletteBetsCleared(string PlayerId, RouletteTable Table) : NetMessage;

public sealed record RouletteReady(string PlayerId, RouletteTable Table) : NetMessage;

/// <summary>No more bets: the wheel is turning and this is where the ball will land. The chips stay on the layout.</summary>
public sealed record RouletteSpun(int Number, RouletteTable Table) : NetMessage;

/// <summary>The ball has landed and the stacks have been paid.</summary>
public sealed record RouletteSettled(int Number, RoulettePayout[] Payouts, RouletteTable Table) : NetMessage;

/// <summary>Brings a late spectator up to date.</summary>
public sealed record RouletteSnapshot(RouletteTable Table) : NetMessage;
