namespace TavernGames.Core;

/// <summary>Source of dice rolls. Abstracted so games can be made deterministic in tests.</summary>
public interface IDiceRoller
{
    /// <summary>Returns a value in the range [1, 6].</summary>
    int Roll();
}

public sealed class RandomDiceRoller : IDiceRoller
{
    private readonly Random _rng;

    public RandomDiceRoller(int? seed = null) =>
        _rng = seed is null ? new Random() : new Random(seed.Value);

    public int Roll() => _rng.Next(1, 7);
}
