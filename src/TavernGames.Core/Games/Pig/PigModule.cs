using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Pig;

// ---------- Client -> Server ----------

public sealed record PigRoll : NetMessage;
public sealed record PigHold : NetMessage;

// ---------- Server -> Client ----------

/// <summary>A seat is on the clock. <paramref name="TurnTotal"/> is non-zero only when catching a spectator up mid-turn.</summary>
public sealed record PigTurnStarted(string CurrentPlayerId, int TurnTotal, int TargetScore, PlayerPublic[] Players) : NetMessage;

public sealed record PigRolled(string PlayerId, int Face, bool Busted, int TurnTotal) : NetMessage;

public sealed record PigHeld(string PlayerId, int Banked, int NewScore) : NetMessage;

/// <summary>
/// The classic "hold at 20" player, with two adjustments: it banks the moment it can
/// win, and it stops playing safe once an opponent is within striking distance.
/// </summary>
public static class PigBot
{
    public static bool ShouldRoll(PigGame game, PigPlayer self, Random rng)
    {
        if (game.TurnTotal == 0) return true;                                   // must roll before holding
        if (self.Score + game.TurnTotal >= game.TargetScore) return false;      // bank the win

        var bestRival = game.Players.Where(p => p.Id != self.Id).Select(p => p.Score).DefaultIfEmpty(0).Max();
        if (bestRival >= game.TargetScore - 15) return true;                    // someone is about to win: race them

        var threshold = 20 + rng.Next(-3, 4);                                   // a little personality
        return game.TurnTotal < threshold;
    }
}

/// <summary>Adapts the <see cref="PigGame"/> engine to the tavern platform.</summary>
public sealed class PigModule : IGameModule
{
    public const string Type = "pig";
    private const string TargetKey = "targetScore";

    public static GameDescriptor Descriptor { get; } = new(
        Type: Type,
        DisplayName: "Pig",
        Blurb: "Roll as long as you dare and bank the total. Roll a 1 and lose it all.",
        Rules: PigRules.Sections,
        MinPlayers: PigGame.MinPlayers,
        MaxPlayers: PigGame.MaxPlayers,
        Options: [new GameOption(TargetKey, "Score to win", 20, 200, PigGame.DefaultTargetScore, "The score you need to bank to win, from 20 to 200. The default is 100.")],
        Messages:
        [
            (typeof(PigRoll), "pig.roll"),
            (typeof(PigHold), "pig.hold"),
            (typeof(PigTurnStarted), "pig.turnStarted"),
            (typeof(PigRolled), "pig.rolled"),
            (typeof(PigHeld), "pig.held"),
        ],
        Create: options => new PigModule(new RandomDiceRoller(), options[TargetKey]));

    private readonly PigGame _game;
    private readonly HashSet<string> _bots = new();

    public PigModule(IDiceRoller roller, int targetScore) => _game = new PigGame(roller, targetScore);

    public string GameType => Type;
    public int MinPlayers => PigGame.MinPlayers;
    public int MaxPlayers => PigGame.MaxPlayers;
    public GamePhase Phase => _game.Phase;
    public string? CurrentActorId => _game.Phase == GamePhase.Playing ? _game.Current.Id : null;

    public PlayerPublic[] Roster() =>
        _game.Players
            .Select(p => new PlayerPublic(p.Id, p.Name, p.Score, Eliminated: false, _bots.Contains(p.Id)))
            .ToArray();

    public void AddPlayer(string id, string name, bool isBot)
    {
        _game.AddPlayer(id, name);
        if (isBot) _bots.Add(id);
    }

    public IReadOnlyList<Emit> RemovePlayer(string id)
    {
        var wasPlaying = _game.Phase == GamePhase.Playing;
        _game.RemovePlayer(id);
        _bots.Remove(id);

        if (!wasPlaying) return [];
        if (_game.Phase == GamePhase.GameOver)
            return [new ToAll(new GameEnded(_game.WinnerId ?? "", Roster()))];
        return [new ToAll(TurnStarted())];
    }

    public IReadOnlyList<Emit> Start()
    {
        _game.StartGame();
        return [new ToAll(TurnStarted())];
    }

    public IReadOnlyList<Emit> Handle(string playerId, NetMessage move) => move switch
    {
        PigRoll => Roll(playerId),
        PigHold => Hold(playerId),
        _ => throw new InvalidOperationException($"'{move.GetType().Name}' is not a Pig move."),
    };

    public NetMessage? DecideBotMove(string botId, Random rng)
    {
        var self = _game.Players.FirstOrDefault(p => p.Id == botId);
        if (self is null || _game.Phase != GamePhase.Playing) return null;
        return PigBot.ShouldRoll(_game, self, rng) ? new PigRoll() : new PigHold();
    }

    public IReadOnlyList<NetMessage> CatchUp() =>
        _game.Phase == GamePhase.Playing ? [TurnStarted()] : [];

    private IReadOnlyList<Emit> Roll(string playerId)
    {
        var result = _game.Roll(playerId);
        var emits = new List<Emit> { new ToAll(new PigRolled(playerId, result.Face, result.Busted, result.TurnTotal)) };
        if (result.Busted)
        {
            emits.Add(new Pause(PauseKind.Beat)); // let the bust land before the next seat starts
            emits.Add(new ToAll(TurnStarted()));
        }
        return emits;
    }

    private IReadOnlyList<Emit> Hold(string playerId)
    {
        var result = _game.Hold(playerId);
        var emits = new List<Emit> { new ToAll(new PigHeld(playerId, result.Banked, result.NewScore)) };
        emits.Add(result.Won
            ? new ToAll(new GameEnded(playerId, Roster()))
            : new ToAll(TurnStarted()));
        return emits;
    }

    private PigTurnStarted TurnStarted() =>
        new(_game.Current.Id, _game.TurnTotal, _game.TargetScore, Roster());
}
