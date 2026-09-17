using Microsoft.AspNetCore.Mvc.Testing;
using LiarsDice.Core.Protocol;

namespace LiarsDice.Server.Tests;

public class SpectatorTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SpectatorTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("LIARSDICE_BOT_DELAY_MS", "0");
        _factory = factory;
    }

    private async Task<WsTestClient> ConnectAsync()
    {
        var http = new Uri(_factory.Server.BaseAddress, "ws");
        var wsUri = new UriBuilder(http) { Scheme = "ws" }.Uri;
        var ws = await _factory.Server.CreateWebSocketClient().ConnectAsync(wsUri, CancellationToken.None);
        return new WsTestClient(ws);
    }

    [Fact]
    public async Task Spectator_WatchesGame_ButNeverReceivesAHand()
    {
        await using var host = await ConnectAsync();
        await using var watcher = await ConnectAsync();

        await host.SendAsync(new CreateRoom("Host", StartingDice: 1));
        var id = await host.ReceiveUntilAsync<Identity>();

        await host.SendAsync(new AddBot());
        RoomUpdate roster;
        do { roster = await host.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 2);

        // Spectator joins by code and is acknowledged with the current roster.
        await watcher.SendAsync(new Spectate(id.RoomCode));
        var ack = await watcher.ReceiveUntilAsync<SpectateAccepted>();
        Assert.Equal(id.RoomCode, ack.RoomCode);
        await watcher.ReceiveUntilAsync<RoomUpdate>();

        await host.SendAsync(new StartGame());

        // Drive the host's turns to finish the game.
        var myTurn = "";
        var standingBid = false;
        for (var step = 0; step < 500; step++)
        {
            var msg = await host.ReceiveAsync(TimeSpan.FromSeconds(10));
            if (msg is GameEnded) break;
            if (msg is RoundStarted r) { myTurn = r.CurrentPlayerId; standingBid = false; }
            else if (msg is BidPlaced b) { myTurn = b.NextPlayerId; standingBid = true; }

            if (myTurn == id.PlayerId)
            {
                if (standingBid) await host.SendAsync(new Challenge());
                else await host.SendAsync(new PlaceBid(1, 2));
            }
        }

        // The spectator should have seen the game unfold and end — but never a private hand.
        var sawRound = false;
        var sawEnd = false;
        for (var step = 0; step < 500 && !sawEnd; step++)
        {
            var msg = await watcher.ReceiveAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotType<YourHand>(msg); // the whole point: watchers get no secret dice
            if (msg is RoundStarted) sawRound = true;
            if (msg is GameEnded) sawEnd = true;
        }

        Assert.True(sawRound, "spectator should have seen a round start");
        Assert.True(sawEnd, "spectator should have seen the game end");
    }
}
