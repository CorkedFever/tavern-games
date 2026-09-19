using TavernGames.Core.Protocol;

namespace TavernGames.Core.Platform;

/// <summary>
/// Hosts a game in-process for solo play against bots, with no server and no network.
/// It fills the same role the relay's GameRoom fills online: it owns one
/// <see cref="IGameModule"/>, serializes access to it, drives the bots, and delivers the
/// very same <see cref="NetMessage"/>s the server would, through a callback. So a client
/// that already speaks the wire protocol can't tell offline play from online, and every
/// game works offline for free.
///
/// Delivery and bot thinking run on short-lived tasks (the caller supplies a delivery
/// callback that must be safe to invoke from any thread, e.g. a concurrent queue's
/// enqueue). A gate keeps the module single-threaded while a bot is thinking.
/// </summary>
public sealed class SoloTable
{
    /// <summary>The one human seat's id. Bots are "bot-1", "bot-2", ...</summary>
    public const string HumanId = "you";
    private const string RoomCode = "SOLO";

    private static readonly string[] BotNames =
    {
        "Botiana", "Clockwork Joe", "Gilbot", "Tonberry King",
        "Cactuar", "Mr. Moogle", "Chocobot", "Dice Goblin",
    };

    private readonly IGameModule _game;
    private readonly Action<NetMessage> _deliver;
    private readonly Action<Exception>? _onFault;
    private readonly int _turnDelayMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _bots = new();
    private readonly Random _rng;
    private int _botLoopActive;
    private volatile bool _disposed;

    public SoloTable(
        string gameType,
        IReadOnlyDictionary<string, int>? options,
        int turnDelayMs,
        string playerName,
        Action<NetMessage> deliver,
        Action<Exception>? onFault = null,
        Random? rng = null)
    {
        _game = GameCatalog.Create(gameType, options);
        _deliver = deliver;
        _onFault = onFault;
        _rng = rng ?? new Random();
        _turnDelayMs = Math.Clamp(turnDelayMs, 0, 5000);

        _game.AddPlayer(HumanId, playerName, isBot: false);

        // Put the client straight into the room lobby, as if it had created a room online.
        _deliver(new Identity(HumanId, RoomCode, HumanId));
        _deliver(RoomUpdate());
    }

    /// <summary>Seats <paramref name="bots"/> opponents and deals the first hand, in one go.</summary>
    public async Task QuickStartAsync(int bots)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            for (var i = 0; i < bots && _game.Roster().Length < _game.MaxPlayers; i++)
                SeatBot();
            var opening = _game.Start();
            _deliver(RoomUpdate()); // now reports Playing, so the client leaves the lobby
            await DeliverAsync(opening);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _deliver(new ErrorMessage(ex.Message));
        }
        finally
        {
            _gate.Release();
        }

        await DriveBotsAsync();
    }

    /// <summary>Handles a message the client "sent", the way the server's dispatch would.</summary>
    public void Submit(NetMessage message) => _ = SubmitAsync(message);

    /// <summary>Awaitable form, so tests can drive a game deterministically.</summary>
    public async Task SubmitAsync(NetMessage message)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            switch (message)
            {
                case AddBot:
                    if (_game.Phase != GamePhase.Lobby)
                        throw new InvalidOperationException("Bots can only be added in the lobby.");
                    if (_game.Roster().Length >= _game.MaxPlayers)
                        throw new InvalidOperationException("The table is full.");
                    SeatBot();
                    _deliver(RoomUpdate());
                    break;

                case RemoveBot remove:
                    if (!_bots.Contains(remove.PlayerId))
                        throw new InvalidOperationException("That player is not a bot.");
                    if (_game.Phase != GamePhase.Lobby)
                        throw new InvalidOperationException("Bots can only be removed in the lobby.");
                    _game.RemovePlayer(remove.PlayerId);
                    _bots.Remove(remove.PlayerId);
                    _deliver(RoomUpdate());
                    break;

                case StartGame:
                    var opening = _game.Start();
                    _deliver(RoomUpdate()); // now reports Playing, so the client leaves the lobby
                    await DeliverAsync(opening);
                    break;

                default:
                    // Any other message is a game move; the module validates it.
                    await DeliverAsync(_game.Handle(HumanId, message));
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _deliver(new ErrorMessage(ex.Message));
        }
        finally
        {
            _gate.Release();
        }

        await DriveBotsAsync();
    }

    /// <summary>Plays moves for bots while the seat on the clock is a bot.</summary>
    public async Task DriveBotsAsync()
    {
        if (Interlocked.CompareExchange(ref _botLoopActive, 1, 0) != 0)
            return; // a loop is already running

        try
        {
            while (!_disposed)
            {
                string botId;
                await _gate.WaitAsync();
                try
                {
                    if (_disposed || !BotIsOnTheClock()) return;
                    botId = _game.CurrentActorId!;
                }
                finally
                {
                    _gate.Release();
                }

                // Think outside the gate so the human can still act (e.g. call liar on a bid).
                if (_turnDelayMs > 0)
                    await Task.Delay(_rng.Next(_turnDelayMs * 6 / 10, _turnDelayMs * 14 / 10));

                await _gate.WaitAsync();
                try
                {
                    if (_disposed) return;
                    // The turn may have moved while the bot was thinking; re-check before acting.
                    if (!BotIsOnTheClock() || _game.CurrentActorId != botId) continue;
                    var move = _game.DecideBotMove(botId, _rng);
                    if (move is null) return;
                    await DeliverAsync(_game.Handle(botId, move));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    _onFault?.Invoke(ex);
                    return;
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _botLoopActive, 0);
        }
    }

    // Caller holds the gate.
    private bool BotIsOnTheClock() =>
        _game.Phase == GamePhase.Playing && _game.CurrentActorId is { } actor && _bots.Contains(actor);

    // Caller holds the gate.
    private void SeatBot()
    {
        var used = _game.Roster().Select(p => p.Name).ToHashSet();
        var name = BotNames.FirstOrDefault(n => !used.Contains(n)) ?? $"Bot {_bots.Count + 1}";
        var id = $"bot-{_bots.Count + 1}";
        _game.AddPlayer(id, name, isBot: true);
        _bots.Add(id);
    }

    // Caller holds the gate.
    private RoomUpdate RoomUpdate() =>
        new(RoomCode, HumanId, _game.GameType, _game.Phase, _game.Roster());

    /// <summary>Plays a module's emits into the client, pausing where the table would breathe. Caller holds the gate.</summary>
    private async Task DeliverAsync(IReadOnlyList<Emit> emits)
    {
        foreach (var emit in emits)
        {
            if (_disposed) return;
            switch (emit)
            {
                case ToAll all:
                    _deliver(all.Message);
                    break;
                case ToPlayer one when one.PlayerId == HumanId:
                    _deliver(one.Message); // a bot's private state has no client to reach
                    break;
                case Pause pause when _turnDelayMs > 0:
                    await Task.Delay(pause.Kind == PauseKind.RoundBreak ? _turnDelayMs + 1500 : _turnDelayMs / 2 + 300);
                    break;
            }
        }
    }

    public void Dispose() => _disposed = true;
}
