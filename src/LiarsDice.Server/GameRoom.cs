using System.Collections.Concurrent;
using LiarsDice.Core;
using LiarsDice.Core.Protocol;

namespace LiarsDice.Server;

/// <summary>
/// One room = one <see cref="LiarsDiceGame"/> plus the connections seated in it.
/// All game mutations funnel through this class under <see cref="_gate"/>, so the
/// engine never sees concurrent access even though sockets fire independently.
///
/// Bots are players with no connection. After any human action (or the start of a
/// round) <see cref="DriveBotsAsync"/> runs the turn forward for as long as the
/// current seat is a bot, pausing briefly between moves so it feels human.
/// </summary>
public sealed class GameRoom
{
    private static readonly string[] BotNames =
    {
        "Botiana", "Clockwork Joe", "Gilbot", "Tonberry King",
        "Cactuar", "Mr. Moogle", "Chocobot", "Dice Goblin",
    };

    private readonly object _gate = new();
    private readonly LiarsDiceGame _game;
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ClientConnection> _spectators = new();
    private readonly HashSet<string> _bots = new();
    private readonly Random _botRng = new();
    private readonly int _botDelayMs;
    private readonly int _roundPauseMs;
    private int _botLoopActive;

    public string Code { get; }
    public string HostId { get; private set; } = "";
    public bool IsEmpty => _connections.IsEmpty && _spectators.IsEmpty;

    public GameRoom(string code, int startingDice, int turnDelayMs = 1500)
    {
        Code = code;
        _game = new LiarsDiceGame(new RandomDiceRoller(), startingDice);

        // Env var overrides the per-room pace (tests set it to 0 for instant play).
        _botDelayMs = int.TryParse(Environment.GetEnvironmentVariable("LIARSDICE_BOT_DELAY_MS"), out var ms)
            ? ms
            : Math.Clamp(turnDelayMs, 0, 5000);

        // A breather after each challenge so players can read the reveal before the next round.
        _roundPauseMs = _botDelayMs == 0 ? 0 : _botDelayMs + 1500;
    }

    public async Task AddPlayerAsync(ClientConnection conn, string name)
    {
        lock (_gate)
        {
            _game.AddPlayer(conn.PlayerId, name);
            _connections[conn.PlayerId] = conn;
            if (HostId == "") HostId = conn.PlayerId;
        }

        await conn.SendAsync(new Identity(conn.PlayerId, Code, HostId));
        await BroadcastRoomUpdateAsync();
    }

    /// <summary>Seats a watcher: they receive every public broadcast but no private hand and can't act.</summary>
    public async Task AddSpectatorAsync(ClientConnection conn)
    {
        _spectators[conn.PlayerId] = conn;

        await conn.SendAsync(new SpectateAccepted(Code));

        // Catch the spectator up to the current state.
        RoomUpdate update;
        RoundStarted? round = null;
        BidPlaced? standing = null;
        lock (_gate)
        {
            update = new RoomUpdate(Code, HostId, _game.Phase, SnapshotPlayers());
            if (_game.Phase == GamePhase.Bidding)
            {
                round = new RoundStarted(_game.Current.Id, SnapshotPlayers());
                if (_game.CurrentBid is { } bid && _game.CurrentBidderId is { } bidder)
                    standing = new BidPlaced(bidder, BidDto.From(bid), _game.Current.Id);
            }
        }

        await conn.SendAsync(update);
        if (round is not null) await conn.SendAsync(round);
        if (standing is not null) await conn.SendAsync(standing);
    }

    /// <summary>Removes a connection whether it was a player or a spectator.</summary>
    public async Task RemoveConnectionAsync(string connectionId)
    {
        if (_spectators.TryRemove(connectionId, out _))
            return; // spectators don't affect the game or roster

        await RemovePlayerAsync(connectionId);
    }

    public async Task RemovePlayerAsync(string playerId)
    {
        bool roundChanged;
        lock (_gate)
        {
            _connections.TryRemove(playerId, out _);
            _game.RemovePlayer(playerId);
            if (HostId == playerId)
                HostId = _connections.Keys.FirstOrDefault() ?? "";
            roundChanged = _game.Phase == GamePhase.Bidding;
        }

        if (IsEmpty) return;
        await BroadcastRoomUpdateAsync();
        if (roundChanged)
        {
            await BroadcastAsync(BuildRoundStartedLocked());
            await DriveBotsAsync(); // a bot may now be on the clock
        }
    }

    public async Task HandleAsync(ClientConnection conn, NetMessage message)
    {
        switch (message)
        {
            case StartGame:
                await StartGameAsync(conn);
                break;
            case PlaceBid bid:
                await PlaceBidAsync(conn, bid);
                break;
            case Challenge:
                await ChallengeAsync(conn);
                break;
            case AddBot:
                await AddBotAsync(conn);
                break;
            case RemoveBot remove:
                await RemoveBotAsync(conn, remove.PlayerId);
                break;
            default:
                await conn.SendAsync(new ErrorMessage($"Unexpected message in room: {message.GetType().Name}."));
                break;
        }
    }

    private async Task AddBotAsync(ClientConnection conn)
    {
        try
        {
            lock (_gate)
            {
                if (conn.PlayerId != HostId)
                    throw new InvalidOperationException("Only the host can add bots.");
                var id = "bot-" + Guid.NewGuid().ToString("N")[..6];
                _game.AddPlayer(id, NextBotName());
                _bots.Add(id);
            }
        }
        catch (Exception ex)
        {
            await conn.SendAsync(new ErrorMessage(ex.Message));
            return;
        }

        await BroadcastRoomUpdateAsync();
    }

    private async Task RemoveBotAsync(ClientConnection conn, string botId)
    {
        try
        {
            lock (_gate)
            {
                if (conn.PlayerId != HostId)
                    throw new InvalidOperationException("Only the host can remove bots.");
                if (!_bots.Contains(botId))
                    throw new InvalidOperationException("That player is not a bot.");
                if (_game.Phase != GamePhase.Lobby)
                    throw new InvalidOperationException("Bots can only be removed in the lobby.");
                _game.RemovePlayer(botId);
                _bots.Remove(botId);
            }
        }
        catch (Exception ex)
        {
            await conn.SendAsync(new ErrorMessage(ex.Message));
            return;
        }

        await BroadcastRoomUpdateAsync();
    }

    private async Task StartGameAsync(ClientConnection conn)
    {
        try
        {
            lock (_gate)
            {
                if (conn.PlayerId != HostId)
                    throw new InvalidOperationException("Only the host can start the game.");
                _game.StartGame();
            }
        }
        catch (Exception ex)
        {
            await conn.SendAsync(new ErrorMessage(ex.Message));
            return;
        }

        await BroadcastRoomUpdateAsync();
        await BroadcastRoundAsync();
        await DriveBotsAsync();
    }

    private async Task PlaceBidAsync(ClientConnection conn, PlaceBid bid)
    {
        try
        {
            await PlaceBidCoreAsync(conn.PlayerId, new Bid(bid.Quantity, bid.FaceValue));
        }
        catch (Exception ex)
        {
            await conn.SendAsync(new ErrorMessage(ex.Message));
            return;
        }
        await DriveBotsAsync();
    }

    private async Task ChallengeAsync(ClientConnection conn)
    {
        try
        {
            await ChallengeCoreAsync(conn.PlayerId);
        }
        catch (Exception ex)
        {
            await conn.SendAsync(new ErrorMessage(ex.Message));
            return;
        }
        await DriveBotsAsync();
    }

    // --- Core moves (also used by the bot driver). Throw on an illegal move. ---

    private async Task PlaceBidCoreAsync(string playerId, Bid bid)
    {
        string nextId;
        lock (_gate)
        {
            _game.PlaceBid(playerId, bid);
            nextId = _game.Current.Id;
        }
        await BroadcastAsync(new BidPlaced(playerId, BidDto.From(bid), nextId));
    }

    private async Task ChallengeCoreAsync(string playerId)
    {
        ChallengeOutcome outcome;
        lock (_gate)
        {
            outcome = _game.Challenge(playerId);
        }

        var reveal = outcome.RevealedHands
            .Select(kv => new HandReveal(kv.Key, kv.Value.ToArray()))
            .ToArray();

        await BroadcastAsync(new ChallengeResolved(
            Bid: BidDto.From(outcome.Bid),
            ChallengerId: outcome.ChallengerId,
            BidderId: outcome.BidderId,
            ActualCount: outcome.ActualCount,
            BidWasValid: outcome.BidWasValid,
            LoserId: outcome.LoserId,
            LoserEliminated: outcome.LoserEliminated,
            Reveal: reveal,
            GameOver: outcome.GameOver,
            WinnerId: outcome.WinnerId,
            NextStarterId: outcome.NextStarterId));

        if (outcome.GameOver)
        {
            GameEnded ended;
            lock (_gate) { ended = new GameEnded(outcome.WinnerId ?? "", SnapshotPlayers()); }
            await BroadcastAsync(ended);
        }
        else
        {
            // Let everyone absorb the reveal/result before the dice re-roll.
            if (_roundPauseMs > 0)
                await Task.Delay(_roundPauseMs);
            await BroadcastRoundAsync();
        }
    }

    /// <summary>Plays moves for bots while the current seat is a bot. Reentrancy-guarded.</summary>
    private async Task DriveBotsAsync()
    {
        if (Interlocked.CompareExchange(ref _botLoopActive, 1, 0) != 0)
            return; // a loop is already running

        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_game.Phase != GamePhase.Bidding || !_bots.Contains(_game.Current.Id))
                        return;
                }

                if (_botDelayMs > 0)
                    await Task.Delay(_botRng.Next(_botDelayMs * 6 / 10, _botDelayMs * 14 / 10));

                string botId;
                BotDecision move;
                lock (_gate)
                {
                    // State may have shifted during the delay (e.g. a disconnect); re-validate.
                    if (_game.Phase != GamePhase.Bidding || !_bots.Contains(_game.Current.Id))
                        return;
                    botId = _game.Current.Id;
                    move = BotStrategy.Decide(_game, _game.Current, _botRng);
                }

                try
                {
                    if (move.IsChallenge && _game.CurrentBid is not null)
                        await ChallengeCoreAsync(botId);
                    else if (!move.IsChallenge)
                        await PlaceBidCoreAsync(botId, move.Bid);
                    else
                        return; // nothing legal to do
                }
                catch
                {
                    return; // state moved under us; bail out cleanly
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _botLoopActive, 0);
        }
    }

    /// <summary>Sends every seated human their private hand, then the public round state.</summary>
    private async Task BroadcastRoundAsync()
    {
        List<(ClientConnection conn, int[] dice)> hands = new();
        NetMessage round;
        lock (_gate)
        {
            foreach (var p in _game.Players)
                if (_connections.TryGetValue(p.Id, out var c))
                    hands.Add((c, p.Dice.ToArray()));
            round = BuildRoundStartedLocked();
        }

        foreach (var (c, dice) in hands)
            await c.SendAsync(new YourHand(dice));
        await BroadcastAsync(round);
    }

    private RoundStarted BuildRoundStartedLocked()
    {
        lock (_gate)
        {
            return new RoundStarted(_game.Current.Id, SnapshotPlayers());
        }
    }

    private async Task BroadcastRoomUpdateAsync()
    {
        RoomUpdate update;
        lock (_gate)
        {
            update = new RoomUpdate(Code, HostId, _game.Phase, SnapshotPlayers());
        }
        await BroadcastAsync(update);
    }

    // Caller must hold _gate.
    private PlayerPublic[] SnapshotPlayers() =>
        _game.Players
            .Select(p => new PlayerPublic(p.Id, p.Name, p.DiceCount, p.IsEliminated, _bots.Contains(p.Id)))
            .ToArray();

    // Caller must hold _gate.
    private string NextBotName()
    {
        var used = _game.Players.Select(p => p.Name).ToHashSet();
        return BotNames.FirstOrDefault(n => !used.Contains(n))
               ?? $"CPU {_game.Players.Count}";
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
