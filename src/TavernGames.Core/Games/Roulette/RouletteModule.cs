using System.Security.Cryptography;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Roulette;

/// <summary>
/// A bot with a system, like everyone at a roulette table: mostly even-money bets, a dozen
/// or a column now and then, and the occasional number for the thrill, each sized to a
/// twelfth or so of its stack.
/// </summary>
public static class RouletteBot
{
    private static readonly RouletteBetKind[] EvenMoney =
    [
        RouletteBetKind.Red, RouletteBetKind.Black, RouletteBetKind.Odd,
        RouletteBetKind.Even, RouletteBetKind.Low, RouletteBetKind.High,
    ];

    public static RoulettePlace ChooseBet(RoulettePlayer self, int minBet, Random rng)
    {
        var units = Math.Max(1, self.Chips / 12 / minBet);
        var amount = Math.Min(self.Chips, units * minBet);

        var roll = rng.Next(100);
        if (roll < 55)
            return new RoulettePlace(EvenMoney[rng.Next(EvenMoney.Length)], 0, amount);
        if (roll < 80)
            return new RoulettePlace(rng.Next(2) == 0 ? RouletteBetKind.Dozen : RouletteBetKind.Column, rng.Next(1, 4), amount);
        return new RoulettePlace(RouletteBetKind.Straight, rng.Next(RouletteGame.Pockets), amount);
    }
}

/// <summary>Adapts the <see cref="RouletteGame"/> engine to the tavern platform.</summary>
public sealed class RouletteModule : IGameModule
{
    public const string Type = "roulette";
    private const string ChipsKey = "startingChips";
    private const string RoundsKey = "rounds";
    private const string MinBetKey = "minBet";

    public static GameDescriptor Descriptor { get; } = new(
        Type: Type,
        DisplayName: "Roulette",
        Blurb: "Chips on the layout, the wheel spins, the ball decides. Most chips after the last spin wins.",
        Rules: RouletteRules.Sections,
        MinPlayers: RouletteGame.MinPlayers,
        MaxPlayers: RouletteGame.MaxPlayers,
        Options:
        [
            new GameOption(ChipsKey, "Starting chips", 100, 5000, 500, "How many chips each player starts with. The default is 500."),
            new GameOption(RoundsKey, "Spins", 3, 30, 10, "How many spins the game lasts. The default is 10."),
            new GameOption(MinBetKey, "Minimum bet", 1, 100, 5, "The smallest bet you can place, which also sets the chip sizes, and a player with fewer chips than this before a spin is out. The default is 5."),
        ],
        Messages:
        [
            (typeof(RoulettePlace), "roulette.place"),
            (typeof(RouletteClear), "roulette.clear"),
            (typeof(RouletteDone), "roulette.done"),
            (typeof(RouletteBettingOpened), "roulette.bettingOpened"),
            (typeof(RouletteBetPlaced), "roulette.betPlaced"),
            (typeof(RouletteBetsCleared), "roulette.betsCleared"),
            (typeof(RouletteReady), "roulette.ready"),
            (typeof(RouletteSpun), "roulette.spun"),
            (typeof(RouletteSettled), "roulette.settled"),
            (typeof(RouletteSnapshot), "roulette.snapshot"),
        ],
        Create: options => new RouletteModule(
            // The pocket comes from the OS's cryptographic generator: nobody at the table can guess it.
            () => RandomNumberGenerator.GetInt32(RouletteGame.Pockets),
            options[ChipsKey], options[RoundsKey], Math.Min(options[MinBetKey], options[ChipsKey])));

    private readonly RouletteGame _game;
    private readonly HashSet<string> _bots = new();

    /// <summary>How many more bets each bot means to place this round, so it doesn't bet forever.</summary>
    private readonly Dictionary<string, (int Round, int Remaining)> _plans = new();

    public RouletteModule(Func<int> spin, int startingChips, int rounds, int minBet) =>
        _game = new RouletteGame(spin, startingChips, rounds, minBet);

    public string GameType => Type;
    public int MinPlayers => RouletteGame.MinPlayers;
    public int MaxPlayers => RouletteGame.MaxPlayers;
    public GamePhase Phase => _game.Phase;

    /// <summary>
    /// Everyone bets at once, so "the seat on the clock" is whichever bot still has bets to
    /// place (bots never wait on a human), or failing that the first human who isn't done.
    /// Between the last "done" and the next round nobody acts: the wheel does.
    /// </summary>
    public string? CurrentActorId
    {
        get
        {
            if (!_game.IsBetting) return null;
            var pending = _game.AwaitingBets.ToList();
            return (pending.FirstOrDefault(p => _bots.Contains(p.Id)) ?? pending.FirstOrDefault())?.Id;
        }
    }

    public PlayerPublic[] Roster() =>
        _game.Players
            .Select(p => new PlayerPublic(p.Id, p.Name, p.Chips, p.Out, _bots.Contains(p.Id)))
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
        _plans.Remove(id);

        if (!wasPlaying) return [];
        var emits = ToEmits(events);
        if (events.Count == 0 && _game.Phase == GamePhase.Playing)
            emits.Add(new ToAll(new RouletteSnapshot(_game.Snapshot()))); // show the table without the departed seat
        return emits;
    }

    public IReadOnlyList<Emit> Start() => ToEmits(_game.StartGame());

    public IReadOnlyList<Emit> Handle(string playerId, NetMessage move) => ToEmits(move switch
    {
        RoulettePlace place => _game.Place(playerId, place.Kind, place.Pick, place.Amount),
        RouletteClear => _game.Clear(playerId),
        RouletteDone => _game.Done(playerId),
        _ => throw new InvalidOperationException($"'{move.GetType().Name}' is not a roulette move."),
    });

    public NetMessage? DecideBotMove(string botId, Random rng)
    {
        var self = _game.Players.FirstOrDefault(p => p.Id == botId);
        if (self is null || !_game.IsBetting || self.Out || self.Done) return null;

        if (!_plans.TryGetValue(botId, out var plan) || plan.Round != _game.Round)
            plan = (_game.Round, rng.Next(1, 4));

        if (plan.Remaining > 0 && self.Chips >= _game.MinBet)
        {
            _plans[botId] = (plan.Round, plan.Remaining - 1);
            return RouletteBot.ChooseBet(self, _game.MinBet, rng);
        }

        _plans[botId] = (plan.Round, 0);
        return new RouletteDone();
    }

    public IReadOnlyList<NetMessage> CatchUp() =>
        _game.Phase == GamePhase.Playing ? [new RouletteSnapshot(_game.Snapshot())] : [];

    /// <summary>Turns the engine's chain of events into messages, with the wheel given time to turn and the result time to be read.</summary>
    private List<Emit> ToEmits(IReadOnlyList<RouletteEvent> events)
    {
        var emits = new List<Emit>();
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case RouletteEventKind.BettingOpened:
                    emits.Add(new ToAll(new RouletteBettingOpened(e.Table)));
                    break;
                case RouletteEventKind.BetPlaced:
                    emits.Add(new ToAll(new RouletteBetPlaced(e.PlayerId!, e.Bet!, e.Table)));
                    break;
                case RouletteEventKind.BetsCleared:
                    emits.Add(new ToAll(new RouletteBetsCleared(e.PlayerId!, e.Table)));
                    break;
                case RouletteEventKind.Ready:
                    emits.Add(new ToAll(new RouletteReady(e.PlayerId!, e.Table)));
                    break;
                case RouletteEventKind.Spun:
                    emits.Add(new Pause(PauseKind.Beat));
                    emits.Add(new ToAll(new RouletteSpun(e.Number, e.Table)));
                    emits.Add(new Pause(PauseKind.RoundBreak)); // the ball is rolling
                    break;
                case RouletteEventKind.Settled:
                    emits.Add(new ToAll(new RouletteSettled(e.Number, e.Payouts ?? [], e.Table)));
                    emits.Add(new Pause(PauseKind.RoundBreak)); // time to see who won what
                    break;
                case RouletteEventKind.GameOver:
                    emits.Add(new ToAll(new GameEnded(e.WinnerId ?? "", Roster())));
                    break;
            }
        }
        return emits;
    }
}
