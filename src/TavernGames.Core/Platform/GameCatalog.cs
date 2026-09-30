using TavernGames.Core.Games.Blackjack;
using TavernGames.Core.Games.Holdem;
using TavernGames.Core.Games.LiarsDice;
using TavernGames.Core.Games.Mia;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Games.Roulette;
using TavernGames.Core.Games.ShipCaptainCrew;

namespace TavernGames.Core.Platform;

/// <summary>
/// A host-tunable integer setting a game exposes at room creation (rendered as a slider).
/// <paramref name="Help"/> is one plain sentence on what it changes, shown as the slider's tooltip.
/// </summary>
public sealed record GameOption(string Key, string Label, int Min, int Max, int Default, string Help = "");

/// <summary>One part of a game's "How to play": a short heading and a few plain sentences.</summary>
public sealed record RulesSection(string Heading, string Text);

/// <summary>
/// Everything the platform needs to know about a game: how to list it, how to explain it,
/// which wire messages it adds, and how to build its module. Adding a game means writing a
/// module, its rules, and adding its descriptor to <see cref="GameCatalog.Games"/>.
/// <paramref name="Rules"/> is the "How to play" the plugin shows, in playing order, and must
/// describe what the engine does rather than the game as played elsewhere.
/// </summary>
public sealed record GameDescriptor(
    string Type,
    string DisplayName,
    string Blurb,
    IReadOnlyList<RulesSection> Rules,
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
        RouletteModule.Descriptor,
        ShipCaptainCrewModule.Descriptor,
    ];

    public static GameDescriptor? Find(string? type) =>
        Games.FirstOrDefault(g => string.Equals(g.Type, type, StringComparison.OrdinalIgnoreCase));

    public static IGameModule Create(string? type, IReadOnlyDictionary<string, int>? options)
    {
        var game = Find(type) ?? throw new InvalidOperationException($"Unknown game '{type}'.");
        return game.Create(game.ResolveOptions(options));
    }
}
