namespace TavernGames.Core.Games.Mia;

public sealed class MiaPlayer
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public int Lives { get; internal set; }

    /// <summary>Out of lives, and out of the game.</summary>
    public bool IsOut => Lives <= 0;
}

public enum MiaEventKind
{
    RoundStarted,
    Rolled,
    Announced,
    Believed,
    Called,
    Conceded,
    GameOver,
}

/// <summary>
/// Something that happened at the table, with the table as it looked at that moment.
/// One move can cause a chain (a call settles the round, someone loses a life, the next
/// round opens and its first roll happens), so every engine call returns the whole chain
/// in order.
/// </summary>
public sealed record MiaEvent(
    MiaEventKind Kind,
    MiaTable Table,
    string? PlayerId = null,
    string? AnnouncerId = null,
    int Value = 0,
    int Revealed = 0,
    bool Honest = false,
    string? LoserId = null,
    int LivesLost = 0,
    bool LoserEliminated = false,
    string? WinnerId = null)
{
    /// <summary>
    /// The dice under the cup, on a <see cref="MiaEventKind.Rolled"/> event only. This is
    /// the game's single secret: the module sends it to the roller alone and must never
    /// copy it into a public message.
    /// </summary>
    public int Secret { get; init; }
}

/// <summary>
/// Mia (Maexchen, Mexico), the two-dice bluffing game, for two to six players.
///
/// The player on turn rolls two dice under a cup where only they can see them, then
/// announces a value to the table. The first announcement of a round is free; every
/// later one must be strictly higher than the one before, and a player may lie in either
/// direction. The next live player either believes it, takes the cup and has to announce
/// something higher still, or calls liar: the announcer's dice are shown, and if they
/// reach the announcement the caller loses a life, otherwise the announcer does. Mia
/// (a 2 and a 1) beats everything and is worth double, and because nothing beats it the
/// player facing one may concede a single life instead of calling. Every life lost ends
/// the round, and the player who lost it starts the next one. Last player with lives wins.
///
/// Where the rules are played differently from table to table, this engine takes the
/// most common line:
/// <list type="bullet">
/// <item>the opening announcement of a round may be anything, Mia included;</item>
/// <item>a roll that exactly matches the announcement is honest, so the caller pays;</item>
/// <item>a two-life Mia loss against a player holding one life takes that last life and
/// no more: lives stop at zero;</item>
/// <item>the cup cannot be passed on unseen, and an announcement cannot be repeated: the
/// player on turn always rolls and always announces something higher.</item>
/// </list>
///
/// This type is the single source of truth. The dice under the cup live here and leave
/// only through <see cref="SecretFor"/>, which answers to their owner and nobody else.
/// </summary>
public sealed class MiaGame
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 6;
    public const int DefaultLives = 3;

    private readonly List<MiaPlayer> _players = new();
    private readonly IDiceRoller _roller;
    private int _turnIndex;
    private MiaValue? _secret;
    private string? _secretOwnerId;

    public MiaGame(IDiceRoller roller, int lives = DefaultLives)
    {
        if (lives is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(lives), "Lives must be 1-6.");
        _roller = roller;
        StartingLives = lives;
    }

    public int StartingLives { get; }
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public MiaStep Step { get; private set; } = MiaStep.None;
    public string? WinnerId { get; private set; }

    /// <summary>The announcement waiting for an answer, if any.</summary>
    public MiaValue? Announced { get; private set; }

    public string? AnnouncerId { get; private set; }

    /// <summary>The last value somebody believed. The next announcement has to beat it.</summary>
    public MiaValue? Accepted { get; private set; }

    public IReadOnlyList<MiaPlayer> Players => _players;

    public string? CurrentPlayerId => Step == MiaStep.None ? null : _players[_turnIndex].Id;

    public int LiveCount => _players.Count(p => !p.IsOut);

    /// <summary>What the seat on the clock may announce, lowest first. Empty when nobody is announcing.</summary>
    public IReadOnlyList<MiaValue> LegalAnnouncements =>
        Step != MiaStep.Announce ? []
        : Accepted is { } accepted ? MiaValue.Ordered.Where(v => v.Beats(accepted)).ToArray()
        : MiaValue.Ordered;

    /// <summary>
    /// The dice under the cup, but only for the player who rolled them. Everyone else,
    /// a bot deciding whether to call included, gets null: the secret has one reader.
    /// </summary>
    public MiaValue? SecretFor(string playerId) => _secretOwnerId == playerId ? _secret : null;

    public MiaPlayer AddPlayer(string id, string name)
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("Players can only join while in the lobby.");
        if (_players.Count >= MaxPlayers)
            throw new InvalidOperationException($"A game of Mia holds at most {MaxPlayers} players.");
        if (_players.Any(p => p.Id == id))
            throw new InvalidOperationException($"Player '{id}' is already at the table.");

        var player = new MiaPlayer { Id = id, Name = name };
        _players.Add(player);
        return player;
    }

    /// <summary>
    /// A mid-game departure. Nobody loses a life because somebody walked out: if the
    /// leaver was one of the two players in the exchange, the round simply starts again
    /// from their empty seat, and the dice they were hiding are thrown away unseen.
    /// </summary>
    public IReadOnlyList<MiaEvent> RemovePlayer(string id)
    {
        var index = _players.FindIndex(p => p.Id == id);
        if (index < 0) return [];

        var wasOnTheClock = Phase == GamePhase.Playing && Step != MiaStep.None && index == _turnIndex;
        var heldTheCup = _secretOwnerId == id;

        _players.RemoveAt(index);
        if (Phase != GamePhase.Playing) return [];

        // Keep the clock pointing at the same seat it did before the list shifted.
        if (index < _turnIndex) _turnIndex--;
        if (_turnIndex >= _players.Count) _turnIndex = 0;

        var events = new List<MiaEvent>();
        if (LiveCount <= 1)
        {
            ClearRound();
            Phase = GamePhase.GameOver;
            WinnerId = _players.FirstOrDefault(p => !p.IsOut)?.Id;
            events.Add(Event(MiaEventKind.GameOver) with { WinnerId = WinnerId });
            return events;
        }

        if (!wasOnTheClock && !heldTheCup) return events; // the exchange is intact; play on

        ClearRound();
        BeginRound(index >= _players.Count ? 0 : index, events);
        return events;
    }

    public IReadOnlyList<MiaEvent> StartGame()
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("The game has already started.");
        if (_players.Count < MinPlayers)
            throw new InvalidOperationException($"Need at least {MinPlayers} players to start.");

        foreach (var p in _players) p.Lives = StartingLives;

        var events = new List<MiaEvent>();
        BeginRound(0, events);
        return events;
    }

    /// <summary>Claims <paramref name="value"/> to the table, truthfully or otherwise.</summary>
    public IReadOnlyList<MiaEvent> Announce(string playerId, MiaValue value)
    {
        var player = RequireTurn(playerId, MiaStep.Announce);
        if (!value.IsValid)
            throw new ArgumentException($"{value.Code} is not a Mia value.", nameof(value));
        if (Accepted is { } accepted && !value.Beats(accepted))
            throw new InvalidOperationException($"You have to announce something higher than {accepted.Describe()}.");

        Announced = value;
        AnnouncerId = player.Id;
        Step = MiaStep.Respond;
        _turnIndex = NextLiveIndex(_turnIndex);

        return [Event(MiaEventKind.Announced, player.Id, value.Code)];
    }

    /// <summary>Takes the announcement at its word, and the cup with it.</summary>
    public IReadOnlyList<MiaEvent> Believe(string playerId)
    {
        var player = RequireTurn(playerId, MiaStep.Respond);
        var announced = Announced!.Value;
        if (announced.IsMia)
            throw new InvalidOperationException("Nothing beats Mia. Call liar or concede.");

        Accepted = announced;
        Announced = null;
        AnnouncerId = null;
        Step = MiaStep.Announce; // the clock already points at the believer: they now hold the cup

        var events = new List<MiaEvent> { Event(MiaEventKind.Believed, player.Id, announced.Code) };
        Roll(events);
        return events;
    }

    /// <summary>
    /// Doubts the announcement and turns the cup over. An honest announcement costs the
    /// caller, a lie costs the announcer, and a Mia is worth two lives either way.
    /// </summary>
    public IReadOnlyList<MiaEvent> CallLiar(string playerId)
    {
        var caller = RequireTurn(playerId, MiaStep.Respond);
        var announced = Announced!.Value;
        var actual = _secret!.Value;
        var announcer = _players.First(p => p.Id == AnnouncerId);

        var honest = !announced.Beats(actual); // the roll reached the claim, so the claim stood up
        var cost = announced.IsMia ? 2 : 1;
        var loser = honest ? caller : announcer;

        // A Mia costs two lives from a player who has two. Against the last one it takes
        // that and no more, and the table is told what was taken, not what it would have
        // cost a fuller purse.
        var before = loser.Lives;
        loser.Lives = Math.Max(0, before - cost);
        var paid = before - loser.Lives;

        // Nobody is on the clock while the reveal is read, and the snapshot is taken after
        // the life is gone so the dice and the new count arrive together.
        Step = MiaStep.None;
        var events = new List<MiaEvent>
        {
            new(MiaEventKind.Called, Snapshot(), caller.Id, announcer.Id, announced.Code, actual.Code,
                honest, loser.Id, paid, loser.IsOut),
        };

        EndRound(loser, events);
        return events;
    }

    /// <summary>Pays a life rather than call a Mia. The dice are never shown.</summary>
    public IReadOnlyList<MiaEvent> Concede(string playerId)
    {
        var player = RequireTurn(playerId, MiaStep.Respond);
        var announced = Announced!.Value;
        if (!announced.IsMia)
            throw new InvalidOperationException("You can only concede when you are facing a Mia.");

        player.Lives = Math.Max(0, player.Lives - 1);

        Step = MiaStep.None;
        var events = new List<MiaEvent>
        {
            new(MiaEventKind.Conceded, Snapshot(), player.Id, AnnouncerId, announced.Code,
                LoserId: player.Id, LivesLost: 1, LoserEliminated: player.IsOut),
        };

        EndRound(player, events);
        return events;
    }

    /// <summary>The table as the public may see it right now.</summary>
    public MiaTable Snapshot() => new(
        StartingLives,
        Step,
        CurrentPlayerId,
        AnnouncerId,
        Announced?.Code ?? 0,
        Accepted?.Code ?? 0,
        _players.Select(p => new MiaSeat(p.Id, p.Lives, p.IsOut)).ToArray());

    // ------------------------------------------------------------ round flow

    private void BeginRound(int startIndex, List<MiaEvent> events)
    {
        _turnIndex = _players[startIndex].IsOut ? NextLiveIndex(startIndex) : startIndex;
        Step = MiaStep.Announce;
        Phase = GamePhase.Playing;

        events.Add(Event(MiaEventKind.RoundStarted, _players[_turnIndex].Id));
        Roll(events);
    }

    /// <summary>Rolls two dice for the seat on the clock. Only that seat ever learns the result.</summary>
    private void Roll(List<MiaEvent> events)
    {
        var player = _players[_turnIndex];
        var value = MiaValue.FromDice(_roller.Roll(), _roller.Roll());
        _secret = value;
        _secretOwnerId = player.Id;

        events.Add(Event(MiaEventKind.Rolled, player.Id) with { Secret = value.Code });
    }

    /// <summary>A life has changed hands: the round is over and the loser takes the cup.</summary>
    private void EndRound(MiaPlayer loser, List<MiaEvent> events)
    {
        var loserIndex = _players.IndexOf(loser);
        var loserIsOut = loser.IsOut;
        ClearRound();

        if (LiveCount <= 1)
        {
            Phase = GamePhase.GameOver;
            WinnerId = _players.FirstOrDefault(p => !p.IsOut)?.Id;
            events.Add(Event(MiaEventKind.GameOver) with { WinnerId = WinnerId });
            return;
        }

        BeginRound(loserIsOut ? NextLiveIndex(loserIndex) : loserIndex, events);
    }

    /// <summary>Wipes everything that belonged to the round, the hidden dice first of all.</summary>
    private void ClearRound()
    {
        _secret = null;
        _secretOwnerId = null;
        Announced = null;
        AnnouncerId = null;
        Accepted = null;
        Step = MiaStep.None;
    }

    // ----------------------------------------------------------------- helpers

    private MiaEvent Event(MiaEventKind kind, string? playerId = null, int value = 0) =>
        new(kind, Snapshot(), playerId, Value: value);

    private int NextLiveIndex(int from)
    {
        for (var step = 1; step <= _players.Count; step++)
        {
            var index = (from + step) % _players.Count;
            if (!_players[index].IsOut) return index;
        }
        return from;
    }

    private MiaPlayer RequireTurn(string playerId, MiaStep step)
    {
        if (Phase != GamePhase.Playing)
            throw new InvalidOperationException($"Action not allowed in phase {Phase}.");
        if (Step != step)
            throw new InvalidOperationException(step == MiaStep.Announce
                ? "There is nothing to announce right now."
                : "There is no announcement to answer.");

        var player = _players[_turnIndex];
        if (player.Id != playerId)
            throw new InvalidOperationException("It is not your turn.");
        return player;
    }
}
