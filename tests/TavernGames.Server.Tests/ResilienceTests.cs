using System.Net.WebSockets;
using System.Text;
using TavernGames.Core;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

/// <summary>What happens when clients misbehave: crash without saying goodbye, or send garbage.</summary>
public class ResilienceTests(TavernFactory factory) : IClassFixture<TavernFactory>
{
    /// <summary>
    /// A socket the room can write to. It records what it was sent, or, when
    /// <see cref="Broken"/>, behaves like a client that crashed: it still reports Open
    /// (no close frame ever arrived) and throws on the first write.
    /// </summary>
    private sealed class FakeSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public bool Broken { get; set; }
        public List<string> Sent { get; } = new();

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (Broken) throw new WebSocketException("The remote party closed the WebSocket connection without completing the close handshake.");
            Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromException<WebSocketReceiveResult>(new NotSupportedException());
    }

    [Fact]
    public async Task ACrashedClient_DoesNotStopTheRestOfTheTableHearingTheGame()
    {
        Environment.SetEnvironmentVariable("TAVERN_BOT_DELAY_MS", "0");
        var room = new GameRoom("TEST", new PigModule(new RandomDiceRoller(1), 50));

        var hostSocket = new FakeSocket();
        var crashedSocket = new FakeSocket();
        var lastSocket = new FakeSocket();
        var host = new ClientConnection(hostSocket);
        var crashed = new ClientConnection(crashedSocket);
        var last = new ClientConnection(lastSocket);
        await room.AddPlayerAsync(host, "Host");
        await room.AddPlayerAsync(crashed, "Crashed");
        await room.AddPlayerAsync(last, "Last");

        crashedSocket.Broken = true; // alt-F4: no close frame, the socket still says Open

        // Must not throw, and both healthy seats (one before, one after the dead one) must hear the game start.
        await room.HandleAsync(host, new StartGame());

        Assert.Contains(hostSocket.Sent, m => m.Contains("pig.turnStarted"));
        Assert.Contains(lastSocket.Sent, m => m.Contains("pig.turnStarted"));
        Assert.True(crashed.IsDead);
        Assert.Equal(WebSocketState.Aborted, crashedSocket.State); // aborted, so its own read loop ends and cleans up
        Assert.Equal(GamePhase.Playing, room.Table.Phase);

        // The cleanup that read loop performs: the table carries on with two.
        await room.RemoveConnectionAsync(crashed.PlayerId);
        Assert.Equal(2, room.Table.Players);
    }

    [Fact]
    public async Task AnOversizedMessage_ClosesTheConnection_InsteadOfGrowingABuffer()
    {
        var wsUri = new UriBuilder(new Uri(factory.Server.BaseAddress, "ws")) { Scheme = "ws" }.Uri;
        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(wsUri, CancellationToken.None);

        var huge = Encoding.UTF8.GetBytes(new string('x', 200 * 1024));
        await socket.SendAsync(huge, WebSocketMessageType.Text, true, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(new byte[1024], timeout.Token);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
    }

    [Fact]
    public async Task GarbageAndOutOfPlaceMessages_GetAnError_AndTheConnectionSurvives()
    {
        await using var client = await factory.ConnectAsync();

        await client.SendAsync(new PigRoll()); // not in a room
        Assert.Contains("Join a room", (await client.ReceiveUntilAsync<ErrorMessage>()).Text);

        await client.SendAsync(new CreateRoom("Me", PigModule.Type));
        await client.ReceiveUntilAsync<Identity>();
        await client.SendAsync(new CreateRoom("Me", PigModule.Type)); // already seated
        Assert.Contains("already in a room", (await client.ReceiveUntilAsync<ErrorMessage>()).Text);

        await client.SendAsync(new LeaveRoom());
        await client.SendAsync(new CreateRoom("Me", PigModule.Type)); // and it still works afterwards
        await client.ReceiveUntilAsync<Identity>();
    }

    [Fact]
    public async Task BeingRemovedFromAVenue_TakesYouOutOfItsTable()
    {
        await using var owner = await factory.ConnectAsync();
        await using var member = await factory.ConnectAsync();
        await owner.SendAsync(new Hello(null, "Owner"));
        await owner.ReceiveUntilAsync<Welcome>();
        await member.SendAsync(new Hello(null, "Member"));
        var memberProfile = (await member.ReceiveUntilAsync<Welcome>()).Profile;

        await owner.SendAsync(new CreateVenue("The Gilded Moogle", ""));
        var venue = (await owner.ReceiveUntilAsync<VenueDetails>()).Venue;
        await member.SendAsync(new JoinVenue(venue.JoinCode!));
        await member.ReceiveUntilAsync<VenueDetails>();

        await owner.SendAsync(new CreateRoom("Owner", PigModule.Type, VenueId: venue.Id));
        var seat = await owner.ReceiveUntilAsync<Identity>();
        await member.SendAsync(new JoinRoom(seat.RoomCode, "Member"));
        await member.ReceiveUntilAsync<Identity>();

        await owner.SendAsync(new KickFromVenue(venue.Id, memberProfile.Id));

        var removed = await member.ReceiveUntilAsync<RemovedFromRoom>();
        Assert.Contains("removed from the venue", removed.Reason);

        // The table now shows only the owner, and the removed player is no longer routed to it.
        RoomUpdate roster;
        do { roster = await owner.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length != 1);
        await member.SendAsync(new PigRoll());
        Assert.Contains("Join a room", (await member.ReceiveUntilAsync<ErrorMessage>()).Text);
    }
}
