using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.LiarsDice;

public sealed record BidDto(int Quantity, int FaceValue)
{
    public Bid ToBid() => new(Quantity, FaceValue);
    public static BidDto From(Bid b) => new(b.Quantity, b.FaceValue);
}

public sealed record HandReveal(string PlayerId, int[] Dice);

// ---------- Client -> Server ----------

public sealed record PlaceBid(int Quantity, int FaceValue) : NetMessage;
public sealed record Challenge : NetMessage;

// ---------- Server -> Client ----------

/// <summary>Broadcast at the start of every round. Each seated human also receives its own <see cref="YourHand"/>.</summary>
public sealed record RoundStarted(string CurrentPlayerId, PlayerPublic[] Players) : NetMessage;

/// <summary>Private: this connection's secret dice for the current round.</summary>
public sealed record YourHand(int[] Dice) : NetMessage;

public sealed record BidPlaced(string PlayerId, BidDto Bid, string NextPlayerId) : NetMessage;

public sealed record ChallengeResolved(
    BidDto Bid,
    string ChallengerId,
    string BidderId,
    int ActualCount,
    bool BidWasValid,
    string LoserId,
    bool LoserEliminated,
    HandReveal[] Reveal,
    bool GameOver,
    string? WinnerId,
    string? NextStarterId) : NetMessage;
