using System.Net.WebSockets;
using System.Text;
using TavernGames.Core.Protocol;
using TavernGames.Server;
using TavernGames.Server.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomManager>();

// Profiles, venues and results persist in one SQLite file (Tavern:DbPath, or env Tavern__DbPath).
// The default folder is deliberately NOT called "data": on Windows that would be the same
// folder as the Data/ sources, and ignoring one in git ignores the other.
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
builder.Services.AddHostedService<CommunityJanitor>();

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

            try
            {
                await DispatchAsync(conn, message, rooms, community, communityHandler);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                await conn.SendAsync(new ErrorMessage(ex.Message)); // a rule said no, in words meant for the player
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not WebSocketException)
            {
                // A bug in handling one message must not drop the player or leak internals.
                log.LogError(ex, "Message {Message} failed for connection {Id}", message.GetType().Name, conn.PlayerId);
                await conn.SendAsync(new ErrorMessage("Something went wrong on the server. Please try again."));
            }
        }
    }
    catch (OperationCanceledException) { /* client went away */ }
    catch (WebSocketException) { /* abrupt close, or we aborted a socket that stopped taking sends */ }
    finally
    {
        await LeaveAsync(rooms, conn);
        log.LogInformation("Connection {Id} closed", conn.PlayerId);
    }
});

app.Run();
return;

// Room lifecycle messages are handled here; everything else in a room is the game's business.
static async Task DispatchAsync(
    ClientConnection conn, NetMessage message, RoomManager rooms, CommunityStore community, CommunityHandler communityHandler)
{
    // Profiles and venues work in or out of a room.
    if (await communityHandler.TryHandleAsync(conn, message)) return;

    switch (message)
    {
        case CreateRoom or JoinRoom or Spectate when conn.Room is not null:
            throw new InvalidOperationException("You are already in a room.");

        case CreateRoom create:
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

            var room = rooms.Create(
                create.GameType, create.Options, Math.Clamp(create.TurnDelayMs, 0, 5000),
                create.VenueId, venueName,
                result => community.RecordResult(result.VenueId, result.GameType, result.ProfileIds, result.WinnerProfileId));
            await SitDownAsync(rooms, room, conn, create.PlayerName);
            break;
        }

        case JoinRoom join:
        {
            if (!rooms.TryGet(join.RoomCode, out var room))
                throw new InvalidOperationException($"No room with code '{join.RoomCode}'.");

            // A venue's tables seat its members only (anyone may still spectate).
            if (room.VenueId is { } tableVenue &&
                (conn.ProfileId is not { } joiner || community.RoleOf(joiner, tableVenue) is null))
                throw new InvalidOperationException($"That table is hosted by {room.VenueName}. Join the venue to take a seat.");

            await SitDownAsync(rooms, room, conn, join.PlayerName);
            break;
        }

        case Spectate spectate:
        {
            if (!rooms.TryGet(spectate.RoomCode, out var room))
                throw new InvalidOperationException($"No room with code '{spectate.RoomCode}'.");
            conn.Room = room;
            conn.IsSpectator = true;
            await room.AddSpectatorAsync(conn);
            break;
        }

        case LeaveRoom:
            await LeaveAsync(rooms, conn);
            break;

        default:
            if (conn.Room is not { } current)
                throw new InvalidOperationException("Join a room first.");
            await current.HandleAsync(conn, message);
            break;
    }
}

// Seats a player, and never leaves a half-made room behind if that fails.
static async Task SitDownAsync(RoomManager rooms, GameRoom room, ClientConnection conn, string? requestedName)
{
    try
    {
        await room.AddPlayerAsync(conn, SeatName(conn, requestedName));
        conn.Room = room;
        conn.IsSpectator = false;
    }
    catch
    {
        if (room.IsEmpty) rooms.Remove(room.Code);
        throw;
    }
}

static async Task LeaveAsync(RoomManager rooms, ClientConnection conn)
{
    if (conn.Room is not { } room) return;
    conn.Room = null;
    conn.IsSpectator = false; // free to take a seat at the next table

    await room.RemoveConnectionAsync(conn.PlayerId);
    if (room.IsEmpty) rooms.Remove(room.Code);
}

// A profile's display name wins, so the name at the table matches the leaderboards.
static string SeatName(ClientConnection conn, string? requested) =>
    conn.DisplayName ?? CommunityStore.CleanName(requested);

// Reassembles whole text frames (a single logical message may span several WebSocket frames).
// Nothing in the protocol is more than a few KB, so a message past the cap is abuse: refuse it
// rather than let one connection grow a buffer without bound.
static async IAsyncEnumerable<string> ReadMessagesAsync(
    WebSocket socket,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
{
    const int MaxMessageBytes = 64 * 1024;
    var buffer = new byte[8 * 1024];
    using var assembled = new MemoryStream();

    while (socket.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result;
        assembled.SetLength(0);
        var received = 0;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                yield break;
            }

            received += result.Count;
            if (received > MaxMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large.", CancellationToken.None);
                yield break;
            }
            assembled.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        // Decode once per message: a multi-byte character can straddle two frames.
        if (assembled.Length > 0)
            yield return Encoding.UTF8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length);
    }
}

// Exposed so integration tests can host the app with WebApplicationFactory.
public partial class Program;
