using System.Net.WebSockets;
using System.Text;
using TavernGames.Core.Protocol;
using TavernGames.Server;
using TavernGames.Server.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomManager>();

// Profiles, venues and results persist in one SQLite file (Tavern:DbPath, or env Tavern__DbPath).
builder.Services.AddSingleton(sp =>
{
    var configured = sp.GetRequiredService<IConfiguration>()["Tavern:DbPath"];
    var path = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "tavern-data", "tavern.db")
        : configured;
    return new TavernDb(path);
});
builder.Services.AddSingleton<CommunityStore>();
builder.Services.AddSingleton<CommunityHandler>();

// Default to port 5050 (matches the plugin's default), overridable via ASPNETCORE_URLS.
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls("http://0.0.0.0:5050");

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => "Tavern Games relay server. Connect a WebSocket to /ws.");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Map("/ws", async (HttpContext ctx, RoomManager rooms, CommunityStore community, CommunityHandler communityHandler, ILoggerFactory logFactory) =>
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

            // Profiles and venues work in or out of a room.
            if (await communityHandler.TryHandleAsync(conn, message)) continue;

            // Room lifecycle messages are handled here; in-game messages delegate to the room.
            switch (message)
            {
                case CreateRoom create when room is null:
                    try
                    {
                        string? venueName = null;
                        if (create.VenueId is { } venueId)
                        {
                            // Hosting for a venue is a staff privilege.
                            var role = conn.ProfileId is { } host ? community.RoleOf(host, venueId) : null;
                            if (role is null or VenueRole.Member)
                                throw new InvalidOperationException("Only a venue's staff can host its tables.");
                            venueName = community.VenueName(venueId);
                        }

                        room = rooms.Create(
                            create.GameType, create.Options, Math.Clamp(create.TurnDelayMs, 0, 5000),
                            create.VenueId, venueName,
                            result => community.RecordResult(result.VenueId, result.GameType, result.ProfileIds, result.WinnerProfileId));
                        conn.RoomCode = room.Code;
                        await room.AddPlayerAsync(conn, SeatName(conn, create.PlayerName));
                    }
                    catch (Exception ex)
                    {
                        if (room is not null && room.IsEmpty) rooms.Remove(room.Code);
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
                            // A venue's tables seat its members only (anyone may still spectate).
                            if (target.VenueId is { } tableVenue &&
                                (conn.ProfileId is not { } joiner || community.RoleOf(joiner, tableVenue) is null))
                                throw new InvalidOperationException(
                                    $"That table is hosted by {target.VenueName}. Join the venue to take a seat.");

                            room = target;
                            conn.RoomCode = room.Code;
                            await room.AddPlayerAsync(conn, SeatName(conn, join.PlayerName));
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
                    if (room is not null)
                    {
                        await LeaveAsync(rooms, room, conn);
                        room = null;
                        conn.RoomCode = null;
                        conn.IsSpectator = false; // free to take a seat at the next table
                    }
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

// A profile's display name wins, so the name at the table matches the leaderboards.
static string SeatName(ClientConnection conn, string? requested) =>
    conn.DisplayName ?? CommunityStore.CleanName(requested);

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
