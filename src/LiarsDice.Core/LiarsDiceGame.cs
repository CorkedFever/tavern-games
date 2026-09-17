namespace LiarsDice.Core;

/// <summary>
/// Authoritative Liar's Dice engine (Perudo-style "common hand").
///
/// Each player rolls their dice secretly. Starting with one player and going
/// clockwise, a player either raises the standing bid or challenges it ("calls
/// liar"). On a challenge all hands are revealed and the matching face is
/// counted across every player: if the table holds at least the bid quantity
/// the bid stands and the challenger loses a die, otherwise the bidder loses a
/// die. A player at zero dice is eliminated; the last player standing wins.
///
/// This type is the single source of truth — the server owns one instance per
/// room and clients only render the state it reports.
/// </summary>
public sealed class LiarsDiceGame
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 6;
    public const int DefaultStartingDice = 5;

    private readonly List<Player> _players = new();
    private readonly IDiceRoller _roller;
    private int _turnIndex;

    public LiarsDiceGame(IDiceRoller roller, int startingDice = DefaultStartingDice)
    {
        if (startingDice is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(startingDice), "Starting dice must be 1-6.");
        _roller = roller;
        StartingDice = startingDice;
    }

    public int StartingDice { get; }
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public Bid? CurrentBid { get; private set; }
    public string? CurrentBidderId { get; private set; }
    public string? WinnerId { get; private set; }

    public IReadOnlyList<Player> Players => _players;

    /// <summary>The player whose turn it is to bid or challenge.</summary>
    public Player Current => _players[_turnIndex];

    public int ActiveCount => _players.Count(p => !p.IsEliminated);

    public Player AddPlayer(string id, string name)
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("Players can only join while in the lobby.");
        if (_players.Count >= MaxPlayers)
            throw new InvalidOperationException($"A game holds at most {MaxPlayers} players.");
        if (_players.Any(p => p.Id == id))
            throw new InvalidOperationException($"Player '{id}' is already in the game.");

        var player = new Player { Id = id, Name = name };
        _players.Add(player);
        return player;
    }

    public void RemovePlayer(string id)
    {
        var idx = _players.FindIndex(p => p.Id == id);
        if (idx < 0) return;

        _players.RemoveAt(idx);
        if (Phase == GamePhase.Lobby) return;

        // Mid-game departure. If the game can no longer continue, end it.
        if (idx < _turnIndex) _turnIndex--;
        if (_turnIndex >= _players.Count) _turnIndex = 0;

        if (ActiveCount <= 1)
        {
            Phase = GamePhase.GameOver;
            WinnerId = _players.FirstOrDefault(p => !p.IsEliminated)?.Id;
        }
        else if (Phase == GamePhase.Bidding && _players[_turnIndex].IsEliminated)
        {
            _turnIndex = NextActiveIndex(_turnIndex);
        }
    }

    public void StartGame()
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("The game has already started.");
        if (_players.Count < MinPlayers)
            throw new InvalidOperationException($"Need at least {MinPlayers} players to start.");

        foreach (var p in _players)
            p.DiceCount = StartingDice;

        BeginRound(0);
    }

    public ChallengeOutcome? PlaceBid(string playerId, Bid bid)
    {
        EnsureBidding();
        if (Current.Id != playerId)
            throw new InvalidOperationException("It is not that player's turn.");
        if (!bid.IsValidShape)
            throw new ArgumentException($"Bid {bid} is not a legal shape.", nameof(bid));
        if (CurrentBid is { } standing && !bid.IsHigherThan(standing))
            throw new InvalidOperationException($"Bid {bid} does not raise the standing bid {standing}.");

        CurrentBid = bid;
        CurrentBidderId = playerId;
        _turnIndex = NextActiveIndex(_turnIndex);
        return null;
    }

    /// <summary>
    /// Calling liar is "open": any active player except the one who made the standing bid
    /// may challenge it at any time, not only the player whose turn it is. (Raising a bid
    /// is still turn-ordered — see <see cref="PlaceBid"/>.)
    /// </summary>
    public ChallengeOutcome Challenge(string playerId)
    {
        EnsureBidding();
        if (CurrentBid is not { } bid || CurrentBidderId is not { } bidderId)
            throw new InvalidOperationException("There is no standing bid to challenge.");

        var challenger = _players.FirstOrDefault(p => p.Id == playerId)
            ?? throw new InvalidOperationException("Unknown player.");
        if (challenger.IsEliminated)
            throw new InvalidOperationException("Eliminated players cannot call liar.");
        if (challenger.Id == bidderId)
            throw new InvalidOperationException("You cannot call liar on your own bid.");

        var actual = _players
            .Where(p => !p.IsEliminated)
            .Sum(p => p.Dice.Count(d => d == bid.FaceValue));
        var bidValid = actual >= bid.Quantity;

        var loser = bidValid
            ? challenger                                 // caller was wrong
            : _players.First(p => p.Id == bidderId);     // bidder was caught
        loser.DiceCount--;

        var reveal = _players
            .Where(p => !p.IsEliminated || p.Id == loser.Id)
            .ToDictionary(p => p.Id, p => (IReadOnlyList<int>)p.Dice.ToArray());

        bool gameOver = ActiveCount <= 1;
        string? winnerId = null;
        string? nextStarterId = null;

        if (gameOver)
        {
            Phase = GamePhase.GameOver;
            winnerId = WinnerId = _players.FirstOrDefault(p => !p.IsEliminated)?.Id;
        }
        else
        {
            var loserIdx = _players.IndexOf(loser);
            var startIdx = loser.IsEliminated ? NextActiveIndex(loserIdx) : loserIdx;
            nextStarterId = _players[startIdx].Id;
            BeginRound(startIdx);
        }

        return new ChallengeOutcome(
            Bid: bid,
            ChallengerId: playerId,
            BidderId: bidderId,
            FaceValue: bid.FaceValue,
            ActualCount: actual,
            BidWasValid: bidValid,
            LoserId: loser.Id,
            LoserRemainingDice: loser.DiceCount,
            LoserEliminated: loser.IsEliminated,
            RevealedHands: reveal,
            GameOver: gameOver,
            WinnerId: winnerId,
            NextStarterId: nextStarterId);
    }

    private void BeginRound(int startIndex)
    {
        foreach (var p in _players)
        {
            p.Dice.Clear();
            if (p.IsEliminated) continue;
            for (var i = 0; i < p.DiceCount; i++)
                p.Dice.Add(_roller.Roll());
        }

        CurrentBid = null;
        CurrentBidderId = null;
        _turnIndex = _players[startIndex].IsEliminated ? NextActiveIndex(startIndex) : startIndex;
        Phase = GamePhase.Bidding;
    }

    private int NextActiveIndex(int from)
    {
        for (var step = 1; step <= _players.Count; step++)
        {
            var idx = (from + step) % _players.Count;
            if (!_players[idx].IsEliminated) return idx;
        }
        return from;
    }

    private void EnsureBidding()
    {
        if (Phase != GamePhase.Bidding)
            throw new InvalidOperationException($"Action not allowed in phase {Phase}.");
    }
}
