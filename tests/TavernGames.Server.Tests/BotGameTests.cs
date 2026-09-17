using Microsoft.AspNetCore.Mvc.Testing;
using TavernGames.Core.Games.LiarsDice;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class BotGameTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BotGameTests(WebApplicationFactory<Program> factory)
    {
        // Make bots act instantly so the test isn't paced by "thinking" delays.
        Environment.SetEnvironmentVariable("TAVERN_BOT_DELAY_MS", "0");
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
    public async Task HumanPlusTwoBots_PlaysToCompletionAutomatically()
    {
        await using var me = await ConnectAsync();

        await me.SendAsync(new CreateRoom("Me", LiarsDiceModule.Type, new() { ["startingDice"] = 2 }));
        var id = await me.ReceiveUntilAsync<Identity>();

        // Fill the table with two bots, then start.
        await me.SendAsync(new AddBot());
        await me.SendAsync(new AddBot());
        // Wait until the roster shows all three before starting.
        RoomUpdate roster;
        do { roster = await me.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 3);
        Assert.Equal(2, roster.Players.Count(p => p.IsBot));

        await me.SendAsync(new StartGame());

        // Drive only my own turns; the server auto-plays the bots between them.
        var myTurn = "";
        var standingBid = false;
        GameEnded? ended = null;

        for (var step = 0; step < 500 && ended is null; step++)
        {
            var msg = await me.ReceiveAsync(TimeSpan.FromSeconds(10));
            switch (msg)
            {
                case RoundStarted r:
                    myTurn = r.CurrentPlayerId;
                    standingBid = false;
                    break;
                case BidPlaced b:
                    myTurn = b.NextPlayerId;
                    standingBid = true;
                    break;
                case GameEnded g:
                    ended = g;
                    continue;
            }

            if (ended is null && myTurn == id.PlayerId)
            {
                // Simple policy: challenge if there's a bid to call, otherwise open low.
                if (standingBid)
                    await me.SendAsync(new Challenge());
                else
                    await me.SendAsync(new PlaceBid(1, 2));
            }
        }

        Assert.NotNull(ended);
        Assert.False(string.IsNullOrEmpty(ended!.WinnerId));
    }
}
