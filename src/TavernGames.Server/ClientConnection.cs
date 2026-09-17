using System.Net.WebSockets;
using System.Text;
using TavernGames.Core.Protocol;

namespace TavernGames.Server;

/// <summary>
/// Wraps a single WebSocket. Sends are serialized through <see cref="_sendGate"/>.
///
/// Sending never throws and never waits forever. A room delivers to many sockets while
/// holding its turn gate, so one client that crashed (its socket still reports Open until
/// a write fails) or stalled must not be able to break or freeze delivery for the rest of
/// the table. A failed or timed-out send aborts just this socket; that ends this
/// connection's read loop, whose cleanup then removes it from its room as a normal leave.
/// </summary>
public sealed class ClientConnection(WebSocket socket)
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public string PlayerId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The room this connection is seated in or watching, if any. The room clears it when it evicts someone.</summary>
    public GameRoom? Room { get; set; }

    public bool IsSpectator { get; set; }

    /// <summary>Set once the client has said <c>Hello</c>. Null means an anonymous guest.</summary>
    public string? ProfileId { get; set; }

    /// <summary>The profile's display name, used at tables so it matches the leaderboards.</summary>
    public string? DisplayName { get; set; }

    /// <summary>True once a send has failed; the socket has been aborted and the connection is on its way out.</summary>
    public bool IsDead { get; private set; }

    public Task SendAsync(NetMessage message) => SendRawAsync(message.Serialize());

    public async Task SendRawAsync(string json)
    {
        if (IsDead || socket.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(json);
        using var timeout = new CancellationTokenSource(SendTimeout);
        try
        {
            await _sendGate.WaitAsync(timeout.Token);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            IsDead = true;
            try { socket.Abort(); } catch { /* already gone */ }
        }
    }
}
