using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.ShipCaptainCrew;

/// <summary>
/// A bot that rolls until it has a crew, as everyone must, then weighs the cargo against the
/// best already held: anything that wouldn't win is thrown again, a nine or better is kept,
/// and a middling load is kept more often than not.
/// </summary>
public static class ShipCaptainCrewBot
{
    public static NetMessage Choose(ShipCaptainCrewGame game, SccPlayer self, Random rng)
    {
        if (self.RollsLeft == ShipCaptainCrewGame.RollsPerTurn || !self.Crew)
            return new SccRoll();

        var toBeat = game.Leader.Score;
        if (self.Score <= toBeat)
            return new SccRoll(); // a tie is only a roll-off, and less is a loss

        if (self.Score >= 9 || (self.Score >= 7 && rng.Next(100) < 65))
            return new SccHold();
        return new SccRoll();
    }
}

/// <summary>Adapts the <see cref="ShipCaptainCrewGame"/> engine to the tavern platform.</summary>
public sealed class ShipCaptainCrewModule : IGameModule
{
    public const string Type = "shipcaptaincrew";
    private const string RoundsKey = "roundsToWin";

    public static GameDescriptor Descriptor { get; } = new(
        Type: Type,
        DisplayName: "Ship, Captain & Crew",
        Blurb: "Five dice, three rolls. A ship, a captain and a crew, in that order, and the last two dice are your cargo. Best cargo takes the round.",
        Rules: ShipCaptainCrewRules.Sections,
        MinPlayers: ShipCaptainCrewGame.MinPlayers,
        MaxPlayers: ShipCaptainCrewGame.MaxPlayers,
        Options: [new GameOption(RoundsKey, "Rounds to win", 1, 5, 3, "How many rounds you must win to take the game. The default is 3.")],
        Messages:
        [
            (typeof(SccRoll), "shipcaptaincrew.roll"),
            (typeof(SccHold), "shipcaptaincrew.hold"),
            (typeof(SccTurnStarted), "shipcaptaincrew.turnStarted"),
            (typeof(SccRolled), "shipcaptaincrew.rolled"),
            (typeof(SccHeld), "shipcaptaincrew.held"),
            (typeof(SccRoundEnded), "shipcaptaincrew.roundEnded"),
            (typeof(SccSnapshot), "shipcaptaincrew.snapshot"),
        ],
        Create: options => new ShipCaptainCrewModule(new RandomDiceRoller(), options[RoundsKey]));

    private readonly ShipCaptainCrewGame _game;
    private readonly HashSet<string> _bots = new();

    public ShipCaptainCrewModule(IDiceRoller roller, int roundsToWin) =>
        _game = new ShipCaptainCrewGame(roller, roundsToWin);

    public string GameType => Type;
    public int MinPlayers => ShipCaptainCrewGame.MinPlayers;
    public int MaxPlayers => ShipCaptainCrewGame.MaxPlayers;
    public GamePhase Phase => _game.Phase;
    public string? CurrentActorId => _game.Current?.Id;

    public PlayerPublic[] Roster() =>
        _game.Players
            .Select(p => new PlayerPublic(p.Id, p.Name, p.Points, false, _bots.Contains(p.Id)))
            .ToArray();

    public void AddPlayer(string id, string name, bool isBot)
    {
        _game.AddPlayer(id, name);
        if (isBot) _bots.Add(id);
    }

    public IReadOnlyList<Emit> RemovePlayer(string id)
    {
        var wasPlaying = _game.Phase == GamePhase.Playing;
        var events = _game.RemovePlayer(id);
        _bots.Remove(id);

        if (!wasPlaying) return [];
        var emits = ToEmits(events);
        if (events.Count == 0 && _game.Phase == GamePhase.Playing)
            emits.Add(new ToAll(new SccSnapshot(_game.Snapshot()))); // show the table without the departed seat
        return emits;
    }

    public IReadOnlyList<Emit> Start() => ToEmits(_game.StartGame());

    public IReadOnlyList<Emit> Handle(string playerId, NetMessage move) => ToEmits(move switch
    {
        SccRoll => _game.Roll(playerId),
        SccHold => _game.Hold(playerId),
        _ => throw new InvalidOperationException($"'{move.GetType().Name}' is not a Ship, Captain and Crew move."),
    });

    public NetMessage? DecideBotMove(string botId, Random rng)
    {
        var self = _game.Current;
        return self is not null && self.Id == botId ? ShipCaptainCrewBot.Choose(_game, self, rng) : null;
    }

    public IReadOnlyList<NetMessage> CatchUp() =>
        _game.Phase == GamePhase.Playing ? [new SccSnapshot(_game.Snapshot())] : [];

    /// <summary>Turns the engine's chain of events into messages, with a breath after each turn and a break after each round.</summary>
    private List<Emit> ToEmits(IReadOnlyList<SccEvent> events)
    {
        var emits = new List<Emit>();
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case SccEventKind.TurnStarted:
                    emits.Add(new ToAll(new SccTurnStarted(e.PlayerId!, e.Table)));
                    break;
                case SccEventKind.Rolled:
                {
                    var seat = e.Table.Seats.First(s => s.PlayerId == e.PlayerId);
                    emits.Add(new ToAll(new SccRolled(e.PlayerId!, seat.Dice, seat.Kept, seat.RollsLeft, e.Table)));
                    break;
                }
                case SccEventKind.Held:
                    emits.Add(new ToAll(new SccHeld(e.PlayerId!, e.Score, e.Table)));
                    emits.Add(new Pause(PauseKind.Beat));
                    break;
                case SccEventKind.RoundEnded:
                    emits.Add(new ToAll(new SccRoundEnded(e.WinnerId, e.Score, e.TieBreak, e.Tied ?? [], e.Table)));
                    emits.Add(new Pause(PauseKind.RoundBreak));
                    break;
                case SccEventKind.GameOver:
                    emits.Add(new ToAll(new GameEnded(e.WinnerId ?? "", Roster())));
                    break;
            }
        }
        return emits;
    }
}
