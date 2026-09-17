namespace TavernGames.Core.Games.LiarsDice;

/// <summary>
/// A bid claims that at least <see cref="Quantity"/> dice showing
/// <see cref="FaceValue"/> exist across every player's hand on the table.
/// </summary>
public readonly record struct Bid(int Quantity, int FaceValue)
{
    public const int MinFace = 1;
    public const int MaxFace = 6;

    public bool IsValidShape =>
        Quantity >= 1 && FaceValue is >= MinFace and <= MaxFace;

    /// <summary>
    /// A raise must increase the quantity, or keep the quantity and increase
    /// the face value. (Higher quantity with any face is also legal.)
    /// </summary>
    public bool IsHigherThan(Bid other) =>
        Quantity > other.Quantity ||
        (Quantity == other.Quantity && FaceValue > other.FaceValue);

    public override string ToString() => $"{Quantity}× [{FaceValue}]";
}
