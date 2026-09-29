namespace TavernGames.Core.Games.Roulette;

public sealed class RoulettePlayer
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public int Chips { get; internal set; }
    public List<RouletteBet> Bets { get; } = new();

    /// <summary>Said their bets are down for this spin.</summary>
    public bool Done { get; internal set; }

    /// <summary>Can't cover the minimum bet any more: sits out the rest of the game.</summary>
    public bool Out { get; internal set; }

    public int Staked => Bets.Sum(b => b.Amount);
}

public enum RouletteEventKind
{
    BettingOpened,
    BetPlaced,
    BetsCleared,
    Ready,
    Spun,
    Settled,
    GameOver,
}

/// <summary>
/// Something that happened at the table, with the table as it looked at that moment. One
/// move can cause a chain (the last seat says done, the wheel spins, the stacks are paid,
/// the next round opens), so every engine call returns the whole chain in order.
/// </summary>
public sealed record RouletteEvent(
    RouletteEventKind Kind,
    RouletteTable Table,
    string? PlayerId = null,
    RouletteBet? Bet = null,
    int Number = -1,
    RoulettePayout[]? Payouts = null,
    string? WinnerId = null);

/// <summary>
/// European roulette against the house, for one to six seats round one wheel. Each round
/// everyone puts chips on the layout, says their bets are down, and the wheel spins once;
/// straight numbers pay 35 to 1, dozens and columns 2 to 1, the even-money bets 1 to 1,
/// and a zero takes every outside bet. After the set number of spins the most chips wins;
/// ties go to the earlier seat.
/// </summary>
public sealed class RouletteGame
{
    public const int MinPlayers = 1;
    public const int MaxPlayers = 6;
    public const int Pockets = 37;
    public const int HistoryLength = 12;

    /// <summary>The pockets clockwise round a European wheel, starting at the zero.</summary>
    public static readonly IReadOnlyList<int> WheelOrder =
    [
        0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10,
        5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26,
    ];

    private static readonly HashSet<int> Reds = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];

    private readonly List<RoulettePlayer> _players = new();
    private readonly List<int> _history = new();
    private readonly Func<int> _spin;
    private bool _betting;

    public RouletteGame(Func<int> spin, int startingChips = 500, int rounds = 10, int minBet = 5)
    {
        if (startingChips < minBet) throw new ArgumentOutOfRangeException(nameof(startingChips));
        if (rounds < 1) throw new ArgumentOutOfRangeException(nameof(rounds));
        if (minBet < 1) throw new ArgumentOutOfRangeException(nameof(minBet));

        _spin = spin;
        StartingChips = startingChips;
        TotalRounds = rounds;
        MinBet = minBet;
    }

    public int StartingChips { get; }
    public int TotalRounds { get; }
    public int MinBet { get; }
    public int Round { get; private set; }
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public string? WinnerId { get; private set; }
    public int? LastNumber { get; private set; }

    public IReadOnlyList<RoulettePlayer> Players => _players;
    public IReadOnlyList<int> History => _history;

    public bool IsBetting => Phase == GamePhase.Playing && _betting;

    /// <summary>Seats that haven't said their bets are down yet.</summary>
    public IEnumerable<RoulettePlayer> AwaitingBets =>
        IsBetting ? _players.Where(p => !p.Out && !p.Done) : [];

    // ------------------------------------------------------------------ the wheel

    public static bool IsRed(int number) => Reds.Contains(number);

    public static bool IsBlack(int number) => number != 0 && !Reds.Contains(number);

    /// <summary>1 for 1 to 12, 2 for 13 to 24, 3 for 25 to 36; 0 for the zero.</summary>
    public static int Dozen(int number) => number == 0 ? 0 : (number - 1) / 12 + 1;

    /// <summary>1 for 1, 4, 7...; 2 for 2, 5, 8...; 3 for 3, 6, 9...; 0 for the zero.</summary>
    public static int Column(int number) => number == 0 ? 0 : (number - 1) % 3 + 1;

    public static bool Wins(RouletteBet bet, int number) => bet.Kind switch
    {
        RouletteBetKind.Straight => bet.Pick == number,
        RouletteBetKind.Red => IsRed(number),
        RouletteBetKind.Black => IsBlack(number),
        RouletteBetKind.Odd => number != 0 && number % 2 == 1,
        RouletteBetKind.Even => number != 0 && number % 2 == 0,
        RouletteBetKind.Low => number is >= 1 and <= 18,
        RouletteBetKind.High => number is >= 19 and <= 36,
        RouletteBetKind.Dozen => Dozen(number) == bet.Pick,
        RouletteBetKind.Column => Column(number) == bet.Pick,
        _ => false,
    };

    /// <summary>What a winning stake comes back as, the stake included: 36 times on a number, 3 on a dozen, 2 on even money.</summary>
    public static int Multiplier(RouletteBetKind kind) => kind switch
    {
        RouletteBetKind.Straight => 36,
        RouletteBetKind.Dozen or RouletteBetKind.Column => 3,
        _ => 2,
    };

    public static int Returned(RouletteBet bet, int number) =>
        Wins(bet, number) ? bet.Amount * Multiplier(bet.Kind) : 0;

    // ------------------------------------------------------------------ seating

    public RoulettePlayer AddPlayer(string id, string name)
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("Players can only join while in the lobby.");
        if (_players.Count >= MaxPlayers)
            throw new InvalidOperationException($"A roulette table seats at most {MaxPlayers}.");
        if (_players.Any(p => p.Id == id))
            throw new InvalidOperationException($"Player '{id}' is already at the table.");

        var player = new RoulettePlayer { Id = id, Name = name };
        _players.Add(player);
        return player;
    }

    /// <summary>A departing player forfeits whatever is on the table. The spin goes ahead without them.</summary>
    public IReadOnlyList<RouletteEvent> RemovePlayer(string id)
    {
        var player = _players.FirstOrDefault(p => p.Id == id);
        if (player is null) return [];
        _players.Remove(player);
        if (Phase != GamePhase.Playing) return [];

        var events = new List<RouletteEvent>();
        if (_players.Count == 0)
        {
            Phase = GamePhase.GameOver;
            return events;
        }

        if (_betting) SpinIfAllReady(events);
        return events;
    }

    public IReadOnlyList<RouletteEvent> StartGame()
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("The game has already started.");
        if (_players.Count < MinPlayers)
            throw new InvalidOperationException("Need at least one player to start.");

        foreach (var p in _players) p.Chips = StartingChips;
        Phase = GamePhase.Playing;

        var events = new List<RouletteEvent>();
        OpenBetting(events);
        return events;
    }

    // ------------------------------------------------------------------ betting

    public IReadOnlyList<RouletteEvent> Place(string playerId, RouletteBetKind kind, int pick, int amount)
    {
        var player = RequireBetting(playerId);
        if (amount < MinBet)
            throw new InvalidOperationException($"The minimum bet is {MinBet}.");
        if (amount > player.Chips)
            throw new InvalidOperationException($"You only have {player.Chips} chips left to bet.");

        pick = kind switch
        {
            RouletteBetKind.Straight when pick is < 0 or >= Pockets => throw new InvalidOperationException("There is no such number on the wheel."),
            RouletteBetKind.Dozen or RouletteBetKind.Column when pick is < 1 or > 3 => throw new InvalidOperationException("Dozens and columns are 1, 2 or 3."),
            RouletteBetKind.Straight or RouletteBetKind.Dozen or RouletteBetKind.Column => pick,
            _ => 0,
        };

        player.Chips -= amount;
        var existing = player.Bets.FindIndex(b => b.Kind == kind && b.Pick == pick);
        var bet = existing >= 0 ? player.Bets[existing] with { Amount = player.Bets[existing].Amount + amount } : new RouletteBet(kind, pick, amount);
        if (existing >= 0) player.Bets[existing] = bet;
        else player.Bets.Add(bet);

        return [Event(RouletteEventKind.BetPlaced, playerId, bet)];
    }

    public IReadOnlyList<RouletteEvent> Clear(string playerId)
    {
        var player = RequireBetting(playerId);
        player.Chips += player.Staked;
        player.Bets.Clear();
        return [Event(RouletteEventKind.BetsCleared, playerId)];
    }

    public IReadOnlyList<RouletteEvent> Done(string playerId)
    {
        var player = RequireBetting(playerId);
        player.Done = true;

        var events = new List<RouletteEvent> { Event(RouletteEventKind.Ready, playerId) };
        SpinIfAllReady(events);
        return events;
    }

    /// <summary>The table as the public may see it right now.</summary>
    public RouletteTable Snapshot() => new(
        Math.Min(Round, TotalRounds), TotalRounds, MinBet, IsBetting, LastNumber, _history.ToArray(),
        _players.Select(p => new RouletteSeat(p.Id, p.Chips, p.Bets.ToArray(), p.Done, p.Out)).ToArray());

    // ------------------------------------------------------------ round flow

    private void OpenBetting(List<RouletteEvent> events)
    {
        Round++;
        foreach (var p in _players)
        {
            p.Bets.Clear();
            p.Done = false;
            if (p.Chips < MinBet) p.Out = true;
        }

        if (Round > TotalRounds || _players.All(p => p.Out))
        {
            EndGame(events);
            return;
        }

        _betting = true;
        events.Add(Event(RouletteEventKind.BettingOpened));
    }

    private void SpinIfAllReady(List<RouletteEvent> events)
    {
        if (AwaitingBets.Any()) return;

        var number = _spin();
        if (number is < 0 or >= Pockets)
            throw new InvalidOperationException($"The wheel has no pocket {number}.");

        _betting = false;
        LastNumber = number;
        _history.Insert(0, number);
        if (_history.Count > HistoryLength) _history.RemoveAt(_history.Count - 1);
        events.Add(Event(RouletteEventKind.Spun, number: number));

        var payouts = new List<RoulettePayout>();
        foreach (var p in _players.Where(p => p.Bets.Count > 0))
        {
            var staked = p.Staked;
            var returned = p.Bets.Sum(b => Returned(b, number));
            p.Chips += returned;
            payouts.Add(new RoulettePayout(p.Id, staked, returned));
        }
        events.Add(Event(RouletteEventKind.Settled, number: number) with { Payouts = payouts.ToArray() });

        OpenBetting(events);
    }

    private void EndGame(List<RouletteEvent> events)
    {
        Phase = GamePhase.GameOver;
        _betting = false;
        // Most chips wins; List order is seat order, and MaxBy keeps the first of equals.
        WinnerId = _players.MaxBy(p => p.Chips)?.Id;
        events.Add(Event(RouletteEventKind.GameOver) with { WinnerId = WinnerId });
    }

    // ----------------------------------------------------------------- helpers

    private RouletteEvent Event(RouletteEventKind kind, string? playerId = null, RouletteBet? bet = null, int number = -1) =>
        new(kind, Snapshot(), playerId, bet, number);

    private RoulettePlayer RequireBetting(string playerId)
    {
        if (!IsBetting)
            throw new InvalidOperationException("Bets are closed.");
        var player = _players.FirstOrDefault(p => p.Id == playerId)
            ?? throw new InvalidOperationException("You're not seated at this table.");
        if (player.Out)
            throw new InvalidOperationException("You're out of chips.");
        if (player.Done)
            throw new InvalidOperationException("Your bets are already down.");
        return player;
    }
}
