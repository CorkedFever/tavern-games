using System.Collections.Concurrent;
using TavernGames.Core;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Server;

/// <summary>
/// One room = one <see cref="IGameModule"/> plus the connections seated in or watching
/// it. The room knows nothing about any game's rules: it owns seating, bots, spectators,
/// pacing and delivery, and hands every game move to the module.
///
/// Every state change and the delivery of its results happen under <see cref="_turn"/>,
/// a single async gate. That keeps the module single-threaded and guarantees the table
/// sees each move's messages in order, even though sockets fire independently (and even
/// through a between-rounds pause). Holding the gate through a pause is deliberate: the
/// module's state has already moved on to the next round, so a move let in mid-pause
/// would be broadcast before the messages that explain it. The price is that input
/// (including leaving) waits out the pause, a few seconds at most. Delivery itself can't
/// stall the gate: a send never throws and gives up on a socket after a short timeout.
///
/// Bots are seats with no connection. After any action <see cref="DriveBotsAsync"/>
/// plays the game forward for as long as the seat on the clock is a bot.
/// </summary>
public sealed class GameRoom
{
    private static readonly string[] BotNames =
    {
        "Botiana", "Clockwork Joe", "Gilbot", "Tonberry King",
        "Cactuar", "Mr. Moogle", "Chocobot", "Dice Goblin",
    };

    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly IGameModule _game;
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ClientConnection> _spectators = new();
    private readonly HashSet<string> _bots = new();
    private readonly Random _botRng = new();
    private readonly int _botDelayMs;
    private readonly Action<GameResult>? _onResult;
    private int _botLoopActive;

    // Profiles seated when the game began: the people a result is recorded for, even if they leave early.
    private List<string> _seatedProfiles = [];
    private bool _resultRecorded;
    private volatile VenueTable _table;

    public string Code { get; }
    public string HostId { get; private set; } = "";
    public bool IsEmpty => _connections.IsEmpty && _spectators.IsEmpty;

    /// <summary>The venue hosting this table, or null for a private table.</summary>
    public string? VenueId { get; }
    public string? VenueName { get; }

    /// <summary>A snapshot for venue listings. Safe to read from any thread.</summary>
    public VenueTable Table => _table;

    public GameRoom(
        string code,
        IGameModule game,
        int turnDelayMs = 1500,
        string? venueId = null,
        string? venueName = null,
        Action<GameResult>? onResult = null)
    {
        Code = code;
        _game = game;
        VenueId = venueId;
        VenueName = venueName;
        _onResult = onResult;
        _table = new VenueTable(code, game.GameType, "", 0, game.MaxPlayers, game.Phase);

        // Env var overrides the per-room pace (tests set it to 0 for instant play).
        _botDelayMs = int.TryParse(Environment.GetEnvironmentVariable("TAVERN_BOT_DELAY_MS"), out var ms)
            ? ms
            : Math.Clamp(turnDelayMs, 0, 5000);
    }

    public async Task AddPlayerAsync(ClientConnection conn, string name)
    {
        await _turn.WaitAsync();
        try
        {
            _game.AddPlayer(conn.PlayerId, name, isBot: false);
            _connections[conn.PlayerId] = conn;
            if (HostId == "") HostId = conn.PlayerId;

            await conn.SendAsync(new Identity(conn.PlayerId, Code, HostId));
            await BroadcastAsync(RoomUpdate());
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Seats a watcher: they receive every public broadcast but no private state and can't act.</summary>
    public async Task AddSpectatorAsync(ClientConnection conn)
    {
        await _turn.WaitAsync();
        try
        {
            _spectators[conn.PlayerId] = conn;
            await conn.SendAsync(new SpectateAccepted(Code));
            await conn.SendAsync(RoomUpdate());
            foreach (var message in _game.CatchUp())
                await conn.SendAsync(message);
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// Takes every seat held by <paramref name="profileId"/> away from the table, telling
    /// that player why. Used when someone stops being a member of the hosting venue: a
    /// door check at join time is not enough, since they may already be sitting down.
    /// </summary>
    public async Task EvictProfileAsync(string profileId, string reason)
    {
        foreach (var conn in _connections.Values.Where(c => c.ProfileId == profileId).ToList())
        {
            conn.Room = null; // their next message must not be routed to this table
            await conn.SendAsync(new RemovedFromRoom(reason));
            await RemoveConnectionAsync(conn.PlayerId);
        }
    }

    /// <summary>Removes a connection whether it was a player or a spectator.</summary>
    public async Task RemoveConnectionAsync(string connectionId)
    {
        if (_spectators.TryRemove(connectionId, out _))
            return; // spectators don't affect the game or roster

        await _turn.WaitAsync();
        try
        {
            if (!_connections.TryRemove(connectionId, out _)) return;

            var emits = _game.RemovePlayer(connectionId);
            if (HostId == connectionId)
                HostId = _connections.Keys.FirstOrDefault() ?? "";

            if (IsEmpty) return;
            await BroadcastAsync(RoomUpdate());
            await DeliverAsync(emits);
        }
        finally
        {
            _turn.Release();
        }

        await DriveBotsAsync(); // a bot may now be on the clock
    }

    public async Task HandleAsync(ClientConnection conn, NetMessage message)
    {
        if (conn.IsSpectator)
        {
            await conn.SendAsync(new ErrorMessage("Spectators can only watch."));
            return;
        }

        await _turn.WaitAsync();
        try
        {
            switch (message)
            {
                case StartGame:
                    RequireHost(conn, "start the game");
                    var opening = _game.Start();
                    _seatedProfiles = _connections.Values
                        .Select(c => c.ProfileId).OfType<string>().Distinct().ToList();
                    await BroadcastAsync(RoomUpdate());
                    await DeliverAsync(opening);
                    break;

                case AddBot:
                    RequireHost(conn, "add bots");
                    var id = "bot-" + Guid.NewGuid().ToString("N")[..6];
                    _game.AddPlayer(id, NextBotName(), isBot: true);
                    _bots.Add(id);
                    await BroadcastAsync(RoomUpdate());
                    break;

                case RemoveBot remove:
                    RequireHost(conn, "remove bots");
                    if (!_bots.Contains(remove.PlayerId))
                        throw new InvalidOperationException("That player is not a bot.");
                    if (_game.Phase != GamePhase.Lobby)
                        throw new InvalidOperationException("Bots can only be removed in the lobby.");
                    _game.RemovePlayer(remove.PlayerId);
                    _bots.Remove(remove.PlayerId);
                    await BroadcastAsync(RoomUpdate());
                    break;

                default:
                    // Anything else is a game move; the module validates it.
                    await DeliverAsync(_game.Handle(conn.PlayerId, message));
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await conn.SendAsync(new ErrorMessage(ex.Message));
            return;
        }
        finally
        {
            _turn.Release();
        }

        await DriveBotsAsync();
    }

    /// <summary>Plays moves for bots while the seat on the clock is a bot. Reentrancy-guarded.</summary>
    private async Task DriveBotsAsync()
    {
        do
        {
            if (Interlocked.CompareExchange(ref _botLoopActive, 1, 0) != 0)
                return; // a loop is already running and will pick this up

            try
            {
                if (!await RunBotLoopAsync())
                    return; // a bot is stuck with no legal move; don't spin on it
            }
            finally
            {
                Interlocked.Exchange(ref _botLoopActive, 0);
            }

            // A move may have landed between the loop's last check and releasing the guard
            // (its own DriveBotsAsync call would have bounced off the guard), so look once more.
        }
        while (await BotIsOnTheClockGatedAsync());
    }

    private async Task<bool> BotIsOnTheClockGatedAsync()
    {
        await _turn.WaitAsync();
        try { return BotIsOnTheClock(); }
        finally { _turn.Release(); }
    }

    /// <summary>Returns true when it ran out of bot turns, false if a bot could not move.</summary>
    private async Task<bool> RunBotLoopAsync()
    {
        while (true)
        {
            if (!await BotIsOnTheClockGatedAsync()) return true;

            // "Thinking" happens outside the gate so humans can still act (e.g. call liar).
            if (_botDelayMs > 0)
                await Task.Delay(_botRng.Next(_botDelayMs * 6 / 10, _botDelayMs * 14 / 10));

            await _turn.WaitAsync();
            try
            {
                // The table may have changed while the bot was thinking; re-check, then decide and act atomically.
                if (!BotIsOnTheClock()) continue;
                var botId = _game.CurrentActorId!;
                var move = _game.DecideBotMove(botId, _botRng);
                if (move is null) return false;
                await DeliverAsync(_game.Handle(botId, move));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return false; // a bot produced an illegal move; stop rather than spin
            }
            finally
            {
                _turn.Release();
            }
        }
    }

    // --- Helpers below require the caller to hold _turn. ---

    private bool BotIsOnTheClock() =>
        !IsEmpty // nobody left to play for: let an abandoned room go quiet
        && _game.Phase == GamePhase.Playing && _game.CurrentActorId is { } actor && _bots.Contains(actor);

    private void RequireHost(ClientConnection conn, string action)
    {
        if (conn.PlayerId != HostId)
            throw new InvalidOperationException($"Only the host can {action}.");
    }

    private RoomUpdate RoomUpdate()
    {
        var roster = _game.Roster();
        RefreshTable(roster);
        return new RoomUpdate(Code, HostId, _game.GameType, _game.Phase, roster, VenueName);
    }

    private void RefreshTable(PlayerPublic[] roster) =>
        _table = new VenueTable(
            Code, _game.GameType,
            roster.FirstOrDefault(p => p.Id == HostId)?.Name ?? "",
            roster.Length, _game.MaxPlayers, _game.Phase);

    /// <summary>Reports a finished game once, for the profiles that sat down at the start.</summary>
    private void RecordResult(GameEnded ended)
    {
        RefreshTable(ended.Players);
        if (_resultRecorded || _onResult is null) return;
        _resultRecorded = true;

        var winnerProfile = _connections.TryGetValue(ended.WinnerId, out var winner) ? winner.ProfileId : null;
        try
        {
            _onResult(new GameResult(VenueId, _game.GameType, _seatedProfiles, winnerProfile));
        }
        catch
        {
            // A bookkeeping failure must never break the table.
        }
    }

    private string NextBotName()
    {
        var used = _game.Roster().Select(p => p.Name).ToHashSet();
        return BotNames.FirstOrDefault(n => !used.Contains(n)) ?? $"CPU {used.Count}";
    }

    /// <summary>Plays a module's emits back to the table in order.</summary>
    private async Task DeliverAsync(IReadOnlyList<Emit> emits)
    {
        foreach (var emit in emits)
        {
            switch (emit)
            {
                case ToAll all:
                    // Record before announcing, so anyone who reacts to "game over" sees the updated standings.
                    if (all.Message is GameEnded ended) RecordResult(ended);
                    await BroadcastAsync(all.Message);
                    break;
                case ToPlayer one when _connections.TryGetValue(one.PlayerId, out var conn):
                    await conn.SendAsync(one.Message);
                    break;
                case Pause pause when _botDelayMs > 0:
                    await Task.Delay(pause.Kind == PauseKind.RoundBreak ? _botDelayMs + 1500 : _botDelayMs / 2 + 300);
                    break;
            }
        }
    }

    private async Task BroadcastAsync(NetMessage message)
    {
        var json = message.Serialize();
        foreach (var conn in _connections.Values)
            await conn.SendRawAsync(json);
        foreach (var spectator in _spectators.Values)
            await spectator.SendRawAsync(json);
    }
}

/// <summary>What a room reports when its game ends. <paramref name="WinnerProfileId"/> is null when a bot or guest won.</summary>
public sealed record GameResult(string? VenueId, string GameType, IReadOnlyCollection<string> ProfileIds, string? WinnerProfileId);
