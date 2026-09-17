namespace TavernGames.Core;

/// <summary>
/// A participant in a game. <see cref="Dice"/> holds the current round's secret
/// roll; <see cref="DiceCount"/> is how many dice the player still owns and is
/// what gets re-rolled each round. A player with zero dice is eliminated.
/// </summary>
public sealed class Player
{
    public required string Id { get; init; }
    public required string Name { get; set; }

    public int DiceCount { get; internal set; }

    /// <summary>The faces rolled this round. Length equals <see cref="DiceCount"/> while a round is live.</summary>
    public List<int> Dice { get; } = new();

    public bool IsEliminated => DiceCount <= 0;
}
