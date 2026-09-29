namespace TavernGames.Core.Games.ShipCaptainCrew;

public sealed class SccPlayer
{
    public required string Id { get; init; }
    public required string Name { get; set; }

    /// <summary>Rounds won.</summary>
    public int Points { get; internal set; }

    // This round's turn.
    public int[] Dice { get; } = new int[ShipCaptainCrewGame.DiceCount];
    public bool[] Kept { get; } = new bool[ShipCaptainCrewGame.DiceCount];
    public int RollsLeft { get; internal set; }
    public bool Done { get; internal set; }

    public bool Ship => HasKept(6);
    public bool Captain => HasKept(5);
    public bool Crew => HasKept(4);

    /// <summary>The two dice not set aside, once the crew is aboard.</summary>
    public int[] Cargo => Crew ? Dice.Where((_, i) => !Kept[i]).ToArray() : [];

    /// <summary>The cargo's total, or 0 without a full complement.</summary>
    public int Score => Cargo.Sum();

    internal void ResetTurn(bool inRound)
    {
        Array.Clear(Dice);
        Array.Clear(Kept);
        RollsLeft = inRound ? ShipCaptainCrewGame.RollsPerTurn : 0;
        Done = !inRound;
    }

    /// <summary>Sets aside the first loose die showing <paramref name="value"/>, if there is one.</summary>
    internal void Keep(int value)
    {
        for (var i = 0; i < Dice.Length; i++)
        {
            if (Kept[i] || Dice[i] != value) continue;
            Kept[i] = true;
            return;
        }
    }

    private bool HasKept(int value)
    {
        for (var i = 0; i < Dice.Length; i++)
            if (Kept[i] && Dice[i] == value) return true;
        return false;
    }
}

public enum SccEventKind
{
    TurnStarted,
    Rolled,
    Held,
    RoundEnded,
    GameOver,
}

/// <summary>
/// Something that happened at the table, with the table as it looked at that moment. One move
/// can cause a chain (a third roll ends the turn, the last turn ends the round, the round wins
/// the game), so every engine call returns the whole chain in order.
/// </summary>
public sealed record SccEvent(
    SccEventKind Kind,
    SccTable Table,
    string? PlayerId = null,
    int Score = 0,
    string? WinnerId = null,
    bool TieBreak = false,
    string[]? Tied = null);

/// <summary>
/// Ship, Captain and Crew: the bar dice game. Five dice, three rolls a turn. A 6 is the
/// ship, a 5 the captain, a 4 the crew, and they must come aboard in that order: a captain
/// rolled before the ship is lost with the next throw. Once all three are aboard the other
/// two dice are the cargo; with rolls left the cargo can be thrown again, both dice, or held.
/// No crew after three rolls is no cargo. The best cargo takes the round; tied seats roll it
/// off between themselves. First to the set number of rounds wins.
/// </summary>
public sealed class ShipCaptainCrewGame
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 6;
    public const int RollsPerTurn = 3;
    public const int DiceCount = 5;

    private readonly IDiceRoller _roller;
    private readonly List<SccPlayer> _players = new();
    private List<string> _contenders = new();
    private int _turnIndex;

    public ShipCaptainCrewGame(IDiceRoller roller, int roundsToWin = 3)
    {
        if (roundsToWin < 1) throw new ArgumentOutOfRangeException(nameof(roundsToWin));
        _roller = roller;
        RoundsToWin = roundsToWin;
    }

    public int RoundsToWin { get; }
    public int Round { get; private set; }
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public string? WinnerId { get; private set; }

    public IReadOnlyList<SccPlayer> Players => _players;

    /// <summary>Who is rolling this round: everyone, or the seats tied for a roll-off.</summary>
    public IReadOnlyList<string> Contenders => _contenders;

    public SccPlayer? Current =>
        Phase == GamePhase.Playing && _turnIndex < _contenders.Count ? Find(_contenders[_turnIndex]) : null;

    /// <summary>The best cargo held so far this round and whose it is; the earlier seat keeps an equal score.</summary>
    public (int Score, string? PlayerId) Leader
    {
        get
        {
            SccPlayer? best = null;
            foreach (var id in _contenders)
            {
                var p = Find(id);
                if (p is { Done: true } && p.Score > 0 && (best is null || p.Score > best.Score))
                    best = p;
            }
            return best is null ? (0, null) : (best.Score, best.Id);
        }
    }

    // ------------------------------------------------------------------ seating

    public SccPlayer AddPlayer(string id, string name)
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("Players can only join while in the lobby.");
        if (_players.Count >= MaxPlayers)
            throw new InvalidOperationException($"The table seats at most {MaxPlayers}.");
        if (_players.Any(p => p.Id == id))
            throw new InvalidOperationException($"Player '{id}' is already at the table.");

        var player = new SccPlayer { Id = id, Name = name };
        _players.Add(player);
        return player;
    }

    /// <summary>A departing player's turn, if it was theirs, passes on; a departing contender leaves the roll-off.</summary>
    public IReadOnlyList<SccEvent> RemovePlayer(string id)
    {
        var player = Find(id);
        if (player is null) return [];
        var index = _contenders.IndexOf(id);
        _players.Remove(player);
        if (Phase != GamePhase.Playing) return [];

        var events = new List<SccEvent>();
        if (_players.Count < MinPlayers)
        {
            // Alone at the table is not a game; whoever stayed takes it.
            Phase = GamePhase.GameOver;
            WinnerId = _players.FirstOrDefault()?.Id;
            events.Add(Event(SccEventKind.GameOver) with { WinnerId = WinnerId });
            return events;
        }

        if (index < 0) return events;
        _contenders.RemoveAt(index);
        if (index < _turnIndex)
        {
            _turnIndex--;
        }
        else if (index == _turnIndex)
        {
            if (_turnIndex < _contenders.Count) events.Add(Event(SccEventKind.TurnStarted, Current!.Id));
            else EndRound(events);
        }
        return events;
    }

    public IReadOnlyList<SccEvent> StartGame()
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("The game has already started.");
        if (_players.Count < MinPlayers)
            throw new InvalidOperationException($"Need at least {MinPlayers} players to start.");

        Phase = GamePhase.Playing;
        var events = new List<SccEvent>();
        StartRound(events, _players.Select(p => p.Id).ToList());
        return events;
    }

    // ------------------------------------------------------------------- the turn

    /// <summary>Throws every loose die, takes the ship, captain and crew as they come, and ends the turn on the third throw.</summary>
    public IReadOnlyList<SccEvent> Roll(string playerId)
    {
        var player = RequireTurn(playerId);
        if (player.RollsLeft <= 0)
            throw new InvalidOperationException("No rolls left.");

        for (var i = 0; i < DiceCount; i++)
            if (!player.Kept[i]) player.Dice[i] = _roller.Roll();
        player.RollsLeft--;

        // In that order, and all three may come in one throw; but a captain without a ship is just a five.
        if (!player.Ship) player.Keep(6);
        if (player.Ship && !player.Captain) player.Keep(5);
        if (player.Captain && !player.Crew) player.Keep(4);

        var events = new List<SccEvent> { Event(SccEventKind.Rolled, player.Id) with { Score = player.Score } };
        if (player.RollsLeft == 0) EndTurn(events, player);
        return events;
    }

    /// <summary>Keeps the cargo as it lies. Only once the crew is aboard, and only after a throw.</summary>
    public IReadOnlyList<SccEvent> Hold(string playerId)
    {
        var player = RequireTurn(playerId);
        if (player.RollsLeft == RollsPerTurn)
            throw new InvalidOperationException("Roll first.");
        if (!player.Crew)
            throw new InvalidOperationException("You need a ship, a captain and a crew before you can hold.");

        var events = new List<SccEvent>();
        EndTurn(events, player);
        return events;
    }

    /// <summary>The table as the public may see it right now.</summary>
    public SccTable Snapshot()
    {
        var (score, leader) = Leader;
        return new SccTable(
            Round, RoundsToWin, Current?.Id, leader is null ? null : score, leader,
            _players.Select(p => new SccSeat(
                p.Id, p.Points, p.Ship, p.Captain, p.Crew, p.Dice.ToArray(), p.Kept.ToArray(),
                p.RollsLeft, p.Score, p.Done, _contenders.Contains(p.Id))).ToArray());
    }

    // ------------------------------------------------------------ round flow

    private void StartRound(List<SccEvent> events, List<string> contenders)
    {
        Round++;
        foreach (var p in _players) p.ResetTurn(contenders.Contains(p.Id));
        _contenders = contenders;
        _turnIndex = 0;
        events.Add(Event(SccEventKind.TurnStarted, Current!.Id));
    }

    private void EndTurn(List<SccEvent> events, SccPlayer player)
    {
        player.Done = true;
        events.Add(Event(SccEventKind.Held, player.Id) with { Score = player.Score });

        _turnIndex++;
        if (_turnIndex < _contenders.Count) events.Add(Event(SccEventKind.TurnStarted, Current!.Id));
        else EndRound(events);
    }

    private void EndRound(List<SccEvent> events)
    {
        var everyone = _players.Select(p => p.Id).ToList();
        var inRound = _contenders.Select(Find).Where(p => p is not null).Select(p => p!).ToList();
        var best = inRound.Count == 0 ? 0 : inRound.Max(p => p.Score);

        if (best == 0)
        {
            // Nobody got a crew aboard: no cargo, no winner, and the round is thrown again.
            events.Add(Event(SccEventKind.RoundEnded));
            StartRound(events, everyone);
            return;
        }

        var tied = inRound.Where(p => p.Score == best).Select(p => p.Id).ToArray();
        if (tied.Length > 1)
        {
            events.Add(Event(SccEventKind.RoundEnded) with { Score = best, TieBreak = true, Tied = tied });
            StartRound(events, tied.ToList());
            return;
        }

        var winner = Find(tied[0])!;
        winner.Points++;
        events.Add(Event(SccEventKind.RoundEnded) with { WinnerId = winner.Id, Score = best });

        if (winner.Points >= RoundsToWin)
        {
            Phase = GamePhase.GameOver;
            WinnerId = winner.Id;
            events.Add(Event(SccEventKind.GameOver) with { WinnerId = winner.Id });
            return;
        }

        StartRound(events, everyone);
    }

    // ----------------------------------------------------------------- helpers

    private SccEvent Event(SccEventKind kind, string? playerId = null) => new(kind, Snapshot(), playerId);

    private SccPlayer? Find(string id) => _players.FirstOrDefault(p => p.Id == id);

    private SccPlayer RequireTurn(string playerId)
    {
        if (Phase != GamePhase.Playing)
            throw new InvalidOperationException("The game isn't being played.");
        var player = Find(playerId) ?? throw new InvalidOperationException("You're not seated at this table.");
        if (Current?.Id != playerId)
            throw new InvalidOperationException("It is not your turn.");
        return player;
    }
}
