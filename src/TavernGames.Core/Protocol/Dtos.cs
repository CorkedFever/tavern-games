namespace TavernGames.Core.Protocol;

/// <summary>Public, non-secret view of a player (everyone may see this).</summary>
public sealed record PlayerPublic(string Id, string Name, int DiceCount, bool Eliminated, bool IsBot = false);

public sealed record BidDto(int Quantity, int FaceValue)
{
    public Bid ToBid() => new(Quantity, FaceValue);
    public static BidDto From(Bid b) => new(b.Quantity, b.FaceValue);
}

public sealed record HandReveal(string PlayerId, int[] Dice);
