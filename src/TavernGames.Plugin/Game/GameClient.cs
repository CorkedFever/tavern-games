using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Dalamud.Plugin.Services;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Game;

public enum ConnectionState { Disconnected, Connecting, Connected }

/// <summary>
/// Manages the WebSocket to the relay server. Inbound messages are parsed off the
/// network thread and queued; the plugin drains <see cref="Inbound"/> on the main
/// thread so all game state mutation stays single-threaded.
/// </summary>
public sealed class GameClient(IPluginLog log) : IDisposable
{
    private readonly ConcurrentQueue<NetMessage> _inbound = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? LastError { get; private set; }

    public ConcurrentQueue<NetMessage> Inbound => _inbound;

    public async Task ConnectAsync(string url)
    {
        await DisconnectAsync();
        State = ConnectionState.Connecting;
        LastError = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            LastError = "Invalid server URL — expected ws://host:port/path.";
            State = ConnectionState.Disconnected;
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();
            _socket = new ClientWebSocket();

            // Bound the connect attempt so the UI can never hang on "Connecting...".
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));
            await _socket.ConnectAsync(uri, connectCts.Token);

            State = ConnectionState.Connected;
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            log.Information("Connected to {Url}", url);
        }
        catch (OperationCanceledException)
        {
            LastError = "Connection timed out — is the server running?";
            State = ConnectionState.Disconnected;
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            State = ConnectionState.Disconnected;
            log.Warning(ex, "Failed to connect to {Url}", url);
            await DisconnectAsync();
        }
    }

    public void Send(NetMessage message)
    {
        if (State != ConnectionState.Connected || _socket is null) return;
        var bytes = Encoding.UTF8.GetBytes(message.Serialize());
        // Fire-and-forget: ClientWebSocket allows one outstanding send; our cadence is low.
        _ = SendInternalAsync(bytes);
    }

    private async Task SendInternalAsync(byte[] bytes)
    {
        try
        {
            if (_socket is { State: WebSocketState.Open })
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Send failed");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (_socket is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        State = ConnectionState.Disconnected;
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                try
                {
                    if (NetMessage.Deserialize(sb.ToString()) is { } msg)
                        _inbound.Enqueue(msg);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Failed to parse inbound message");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.Warning(ex, "Receive loop ended");
        }
        finally
        {
            State = ConnectionState.Disconnected;
        }
    }

    /// <summary>Graceful close used by the UI (Disconnect/Leave). Safe to call repeatedly.</summary>
    public async Task DisconnectAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }

        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token);
            }
            catch { /* server already gone, or close timed out — fine */ }
        }

        try { socket?.Dispose(); } catch { /* ignore */ }
        _socket = null;
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
        State = ConnectionState.Disconnected;
    }

    /// <summary>
    /// Synchronous teardown for plugin unload. Deliberately non-blocking: it aborts the
    /// socket rather than awaiting the close handshake, so unloading can never deadlock.
    /// </summary>
    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _socket?.Abort(); } catch { /* ignore */ }
        try { _socket?.Dispose(); } catch { /* ignore */ }
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _socket = null;
        _cts = null;
        State = ConnectionState.Disconnected;
    }
}
