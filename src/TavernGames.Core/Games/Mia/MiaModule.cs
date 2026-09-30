using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Mia;

/// <summary>Adapts the <see cref="MiaGame"/> engine to the tavern platform.</summary>
public sealed class MiaModule : IGameModule
{
    public const string Type = "mia";
    private const string LivesKey = "lives";

    public static GameDescriptor Descriptor { get; } = new(
        Type: Type,
        DisplayName: "Mia",
        Blurb: "Roll two dice under the cup and announce something higher than the last player. Lie if you have to.",
        Rules: MiaRules.Sections,
        MinPlayers: MiaGame.MinPlayers,
        MaxPlayers: MiaGame.MaxPlayers,
        Options: [new GameOption(LivesKey, "Lives per player", 1, 6, MiaGame.DefaultLives, "How many lives each player starts with, from 1 to 6. The default is 3.")],
        Messages:
        [
            (typeof(MiaAnnounce), "mia.announce"),
            (typeof(MiaBelieve), "mia.believe"),
            (typeof(MiaCallLiar), "mia.callLiar"),
            (typeof(MiaConcede), "mia.concede"),
            (typeof(MiaRoundStarted), "mia.roundStarted"),
            (typeof(MiaRolled), "mia.rolled"),
            (typeof(MiaYourRoll), "mia.yourRoll"),
            (typeof(MiaAnnounced), "mia.announced"),
            (typeof(MiaBelieved), "mia.believed"),
            (typeof(MiaCalled), "mia.called"),
            (typeof(MiaConceded), "mia.conceded"),
            (typeof(MiaSnapshot), "mia.snapshot"),
        ],
        Create: options => new MiaModule(new RandomDiceRoller(), options[LivesKey]));

    private readonly MiaGame _game;
    private readonly HashSet<string> _bots = new();

    public MiaModule(IDiceRoller roller, int lives) => _game = new MiaGame(roller, lives);

    public string GameType => Type;
    public int MinPlayers => MiaGame.MinPlayers;
    public int MaxPlayers => MiaGame.MaxPlayers;
    public GamePhase Phase => _game.Phase;

    public string? CurrentActorId => _game.Phase == GamePhase.Playing ? _game.CurrentPlayerId : null;

    public PlayerPublic[] Roster() =>
        _game.Players
            .Select(p => new PlayerPublic(p.Id, p.Name, p.Lives, p.IsOut, _bots.Contains(p.Id)))
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
            emits.Add(new ToAll(new MiaSnapshot(_game.Snapshot()))); // show the table without the departed seat
        return emits;
    }

    public IReadOnlyList<Emit> Start() => ToEmits(_game.StartGame());

    public IReadOnlyList<Emit> Handle(string playerId, NetMessage move) => ToEmits(move switch
    {
        MiaAnnounce announce => _game.Announce(playerId, MiaValue.FromCode(announce.Value)),
        MiaBelieve => _game.Believe(playerId),
        MiaCallLiar => _game.CallLiar(playerId),
        MiaConcede => _game.Concede(playerId),
        _ => throw new InvalidOperationException($"'{move.GetType().Name}' is not a Mia move."),
    });

    public NetMessage? DecideBotMove(string botId, Random rng)
    {
        var self = _game.Players.FirstOrDefault(p => p.Id == botId);
        if (self is null || _game.Phase != GamePhase.Playing || _game.CurrentPlayerId != botId) return null;
        return MiaBot.Decide(_game, self, rng);
    }

    public IReadOnlyList<NetMessage> CatchUp() =>
        _game.Phase == GamePhase.Playing ? [new MiaSnapshot(_game.Snapshot())] : [];

    /// <summary>Turns the engine's chain of events into messages, with pauses where a table would breathe.</summary>
    private List<Emit> ToEmits(IReadOnlyList<MiaEvent> events)
    {
        var emits = new List<Emit>();
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case MiaEventKind.RoundStarted:
                    emits.Add(new ToAll(new MiaRoundStarted(e.PlayerId!, e.Table)));
                    break;

                case MiaEventKind.Rolled:
                    // The only private message in the game: the dice go to the roller alone,
                    // and the table learns no more than that the cup was shaken.
                    emits.Add(new ToPlayer(e.PlayerId!, new MiaYourRoll(e.Secret)));
                    emits.Add(new ToAll(new MiaRolled(e.PlayerId!, e.Table)));
                    break;

                case MiaEventKind.Announced:
                    emits.Add(new ToAll(new MiaAnnounced(e.PlayerId!, e.Value, e.Table)));
                    break;

                case MiaEventKind.Believed:
                    emits.Add(new ToAll(new MiaBelieved(e.PlayerId!, e.Value, e.Table)));
                    break;

                case MiaEventKind.Called:
                    emits.Add(new ToAll(new MiaCalled(
                        e.PlayerId!, e.AnnouncerId!, e.Value, e.Revealed, e.Honest,
                        e.LoserId!, e.LivesLost, e.LoserEliminated, e.Table)));
                    emits.Add(new Pause(PauseKind.RoundBreak)); // time to read the dice before they are swept up
                    break;

                case MiaEventKind.Conceded:
                    emits.Add(new ToAll(new MiaConceded(
                        e.PlayerId!, e.AnnouncerId!, e.Value, e.LoserEliminated, e.Table)));
                    emits.Add(new Pause(PauseKind.RoundBreak));
                    break;

                case MiaEventKind.GameOver:
                    emits.Add(new ToAll(new GameEnded(e.WinnerId ?? "", Roster())));
                    break;
            }
        }
        return emits;
    }
}
