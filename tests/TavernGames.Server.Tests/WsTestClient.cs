using System.Net.WebSockets;
using System.Text;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

/// <summary>Thin test harness around a single WebSocket connection to the relay.</summary>
public sealed class WsTestClient(WebSocket socket) : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[16 * 1024];

    public async Task SendAsync(NetMessage message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.Serialize());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>Receives messages, discarding non-matches, until one of type <typeparamref name="T"/> arrives.</summary>
    public async Task<T> ReceiveUntilAsync<T>(TimeSpan? timeout = null) where T : NetMessage
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var msg = await ReceiveOneAsync(cts.Token);
            if (msg is T typed) return typed;
        }
    }

    /// <summary>Receives the next single message of any type.</summary>
    public async Task<NetMessage?> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        return await ReceiveOneAsync(cts.Token);
    }

    private async Task<NetMessage?> ReceiveOneAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(_buffer, ct);
            sb.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);
        return NetMessage.Deserialize(sb.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        socket.Dispose();
    }
}
