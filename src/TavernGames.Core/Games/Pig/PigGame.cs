namespace TavernGames.Core.Games.Pig;

public sealed class PigPlayer
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public int Score { get; internal set; }
}

public readonly record struct RollResult(int Face, bool Busted, int TurnTotal, string NextPlayerId);
public readonly record struct HoldResult(int Banked, int NewScore, bool Won, string NextPlayerId);

/// <summary>
/// Pig, the push-your-luck dice game. On your turn you roll one die as often as you
/// dare, adding each roll to a turn total. Hold to bank the total into your score; roll
/// a 1 and the turn total is lost and play passes on. First to the target score wins.
/// There is no hidden information, so the whole state is public.
/// </summary>
public sealed class PigGame
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 6;
    public const int DefaultTargetScore = 100;

    private readonly List<PigPlayer> _players = new();
    private readonly IDiceRoller _roller;
    private int _turnIndex;

    public PigGame(IDiceRoller roller, int targetScore = DefaultTargetScore)
    {
        if (targetScore < 2)
            throw new ArgumentOutOfRangeException(nameof(targetScore), "Target score must be at least 2.");
        _roller = roller;
        TargetScore = targetScore;
    }

    public int TargetScore { get; }
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public int TurnTotal { get; private set; }
    public string? WinnerId { get; private set; }

    public IReadOnlyList<PigPlayer> Players => _players;
    public PigPlayer Current => _players[_turnIndex];

    public PigPlayer AddPlayer(string id, string name)
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("Players can only join while in the lobby.");
        if (_players.Count >= MaxPlayers)
            throw new InvalidOperationException($"A game holds at most {MaxPlayers} players.");
        if (_players.Any(p => p.Id == id))
            throw new InvalidOperationException($"Player '{id}' is already in the game.");

        var player = new PigPlayer { Id = id, Name = name };
        _players.Add(player);
        return player;
    }

    public void RemovePlayer(string id)
    {
        var idx = _players.FindIndex(p => p.Id == id);
        if (idx < 0) return;

        var wasCurrent = Phase == GamePhase.Playing && idx == _turnIndex;
        _players.RemoveAt(idx);
        if (Phase != GamePhase.Playing) return;

        if (_players.Count <= 1)
        {
            Phase = GamePhase.GameOver;
            WinnerId = _players.FirstOrDefault()?.Id;
            return;
        }

        if (idx < _turnIndex) _turnIndex--;
        if (_turnIndex >= _players.Count) _turnIndex = 0;
        if (wasCurrent) TurnTotal = 0; // the next seat slid into this index with a fresh turn
    }

    public void StartGame()
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("The game has already started.");
        if (_players.Count < MinPlayers)
            throw new InvalidOperationException($"Need at least {MinPlayers} players to start.");

        _turnIndex = 0;
        TurnTotal = 0;
        Phase = GamePhase.Playing;
    }

    public RollResult Roll(string playerId)
    {
        EnsureTurn(playerId);

        var face = _roller.Roll();
        if (face == 1)
        {
            TurnTotal = 0;
            Advance();
            return new RollResult(face, Busted: true, TurnTotal: 0, NextPlayerId: Current.Id);
        }

        TurnTotal += face;
        return new RollResult(face, Busted: false, TurnTotal, NextPlayerId: Current.Id);
    }

    public HoldResult Hold(string playerId)
    {
        EnsureTurn(playerId);
        if (TurnTotal == 0)
            throw new InvalidOperationException("Roll at least once before holding.");

        var player = Current;
        var banked = TurnTotal;
        player.Score += banked;
        TurnTotal = 0;

        if (player.Score >= TargetScore)
        {
            Phase = GamePhase.GameOver;
            WinnerId = player.Id;
            return new HoldResult(banked, player.Score, Won: true, NextPlayerId: player.Id);
        }

        Advance();
        return new HoldResult(banked, player.Score, Won: false, NextPlayerId: Current.Id);
    }

    private void Advance() => _turnIndex = (_turnIndex + 1) % _players.Count;

    private void EnsureTurn(string playerId)
    {
        if (Phase != GamePhase.Playing)
            throw new InvalidOperationException($"Action not allowed in phase {Phase}.");
        if (Current.Id != playerId)
            throw new InvalidOperationException("It is not that player's turn.");
    }
}
