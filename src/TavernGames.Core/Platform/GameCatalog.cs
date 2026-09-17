using TavernGames.Core.Games.Blackjack;
using TavernGames.Core.Games.Holdem;
using TavernGames.Core.Games.LiarsDice;
using TavernGames.Core.Games.Mia;
using TavernGames.Core.Games.Pig;

namespace TavernGames.Core.Platform;

/// <summary>A host-tunable integer setting a game exposes at room creation (rendered as a slider).</summary>
public sealed record GameOption(string Key, string Label, int Min, int Max, int Default);

/// <summary>
/// Everything the platform needs to know about a game: how to list it, which wire
/// messages it adds, and how to build its module. Adding a game means writing a
/// module and adding its descriptor to <see cref="GameCatalog.Games"/>.
/// </summary>
public sealed record GameDescriptor(
    string Type,
    string DisplayName,
    string Blurb,
    int MinPlayers,
    int MaxPlayers,
    IReadOnlyList<GameOption> Options,
    IReadOnlyList<(Type Type, string Name)> Messages,
    Func<IReadOnlyDictionary<string, int>, IGameModule> Create)
{
    /// <summary>Fills in defaults and clamps whatever the client sent to each option's legal range.</summary>
    public IReadOnlyDictionary<string, int> ResolveOptions(IReadOnlyDictionary<string, int>? given)
    {
        var resolved = new Dictionary<string, int>();
        foreach (var option in Options)
        {
            var value = given is not null && given.TryGetValue(option.Key, out var v) ? v : option.Default;
            resolved[option.Key] = Math.Clamp(value, option.Min, option.Max);
        }
        return resolved;
    }
}

public static class GameCatalog
{
    public static IReadOnlyList<GameDescriptor> Games { get; } =
    [
        LiarsDiceModule.Descriptor,
        PigModule.Descriptor,
        BlackjackModule.Descriptor,
        HoldemModule.Descriptor,
        MiaModule.Descriptor,
    ];

    public static GameDescriptor? Find(string? type) =>
        Games.FirstOrDefault(g => string.Equals(g.Type, type, StringComparison.OrdinalIgnoreCase));

    public static IGameModule Create(string? type, IReadOnlyDictionary<string, int>? options)
    {
        var game = Find(type) ?? throw new InvalidOperationException($"Unknown game '{type}'.");
        return game.Create(game.ResolveOptions(options));
    }
}
