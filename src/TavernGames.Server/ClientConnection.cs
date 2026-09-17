using System.Net.WebSockets;
using System.Text;
using TavernGames.Core.Protocol;

namespace TavernGames.Server;

/// <summary>Wraps a single WebSocket. Sends are serialized through <see cref="_sendGate"/>.</summary>
public sealed class ClientConnection(WebSocket socket)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public string PlayerId { get; } = Guid.NewGuid().ToString("N")[..8];
    public string? RoomCode { get; set; }
    public bool IsSpectator { get; set; }

    public Task SendAsync(NetMessage message) => SendRawAsync(message.Serialize());

    public async Task SendRawAsync(string json)
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync();
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
