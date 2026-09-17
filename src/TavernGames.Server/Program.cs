using System.Net.WebSockets;
using System.Text;
using TavernGames.Core.Protocol;
using TavernGames.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomManager>();

// Default to port 5050 (matches the plugin's default), overridable via ASPNETCORE_URLS.
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls("http://0.0.0.0:5050");

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => "Tavern Games relay server. Connect a WebSocket to /ws.");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Map("/ws", async (HttpContext ctx, RoomManager rooms, ILoggerFactory logFactory) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var log = logFactory.CreateLogger("Ws");
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var conn = new ClientConnection(socket);
    GameRoom? room = null;
    log.LogInformation("Connection {Id} opened", conn.PlayerId);

    try
    {
        await foreach (var json in ReadMessagesAsync(socket, ctx.RequestAborted))
        {
            NetMessage? message;
            try { message = NetMessage.Deserialize(json); }
            catch (Exception ex)
            {
                await conn.SendAsync(new ErrorMessage($"Malformed message: {ex.Message}"));
                continue;
            }
            if (message is null) continue;

            // Room lifecycle messages are handled here; in-game messages delegate to the room.
            switch (message)
            {
                case CreateRoom create when room is null:
                    try
                    {
                        room = rooms.Create(create.GameType, create.Options, Math.Clamp(create.TurnDelayMs, 0, 5000));
                        conn.RoomCode = room.Code;
                        await room.AddPlayerAsync(conn, Sanitize(create.PlayerName));
                    }
                    catch (Exception ex)
                    {
                        room = null;
                        conn.RoomCode = null;
                        await conn.SendAsync(new ErrorMessage(ex.Message));
                    }
                    break;

                case JoinRoom join when room is null:
                    if (rooms.TryGet(join.RoomCode, out var target))
                    {
                        try
                        {
                            room = target;
                            conn.RoomCode = room.Code;
                            await room.AddPlayerAsync(conn, Sanitize(join.PlayerName));
                        }
                        catch (Exception ex)
                        {
                            room = null;
                            conn.RoomCode = null;
                            await conn.SendAsync(new ErrorMessage(ex.Message));
                        }
                    }
                    else
                    {
                        await conn.SendAsync(new ErrorMessage($"No room with code '{join.RoomCode}'."));
                    }
                    break;

                case Spectate spectate when room is null:
                    if (rooms.TryGet(spectate.RoomCode, out var watchRoom))
                    {
                        room = watchRoom;
                        conn.RoomCode = room.Code;
                        conn.IsSpectator = true;
                        await room.AddSpectatorAsync(conn);
                    }
                    else
                    {
                        await conn.SendAsync(new ErrorMessage($"No room with code '{spectate.RoomCode}'."));
                    }
                    break;

                case CreateRoom or JoinRoom or Spectate:
                    await conn.SendAsync(new ErrorMessage("You are already in a room."));
                    break;

                case LeaveRoom:
                    if (room is not null) { await LeaveAsync(rooms, room, conn); room = null; }
                    break;

                default:
                    if (room is null)
                        await conn.SendAsync(new ErrorMessage("Join a room first."));
                    else
                        await room.HandleAsync(conn, message);
                    break;
            }
        }
    }
    catch (OperationCanceledException) { /* client went away */ }
    catch (WebSocketException) { /* abrupt close */ }
    finally
    {
        if (room is not null) await LeaveAsync(rooms, room, conn);
        log.LogInformation("Connection {Id} closed", conn.PlayerId);
    }
});

app.Run();
return;

static async Task LeaveAsync(RoomManager rooms, GameRoom room, ClientConnection conn)
{
    await room.RemoveConnectionAsync(conn.PlayerId);
    if (room.IsEmpty) rooms.Remove(room.Code);
}

static string Sanitize(string? name)
{
    name = (name ?? "").Trim();
    if (name.Length == 0) return "Player";
    return name.Length > 24 ? name[..24] : name;
}

// Reassembles whole text frames (a single logical message may span several WebSocket frames).
static async IAsyncEnumerable<string> ReadMessagesAsync(
    WebSocket socket,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
{
    var buffer = new byte[8 * 1024];
    var sb = new StringBuilder();

    while (socket.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result;
        sb.Clear();
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                yield break;
            }
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        if (sb.Length > 0) yield return sb.ToString();
    }
}

// Exposed so integration tests can host the app with WebApplicationFactory.
public partial class Program;
