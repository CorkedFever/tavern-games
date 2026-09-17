namespace TavernGames.Core.Games.LiarsDice;

/// <summary>The outcome of a resolved challenge, including the revealed hands.</summary>
public sealed record ChallengeOutcome(
    Bid Bid,
    string ChallengerId,
    string BidderId,
    int FaceValue,
    int ActualCount,
    bool BidWasValid,
    string LoserId,
    int LoserRemainingDice,
    bool LoserEliminated,
    IReadOnlyDictionary<string, IReadOnlyList<int>> RevealedHands,
    bool GameOver,
    string? WinnerId,
    string? NextStarterId);
