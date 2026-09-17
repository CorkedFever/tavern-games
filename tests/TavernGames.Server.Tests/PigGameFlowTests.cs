using Microsoft.AspNetCore.Mvc.Testing;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class PigGameFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PigGameFlowTests(WebApplicationFactory<Program> factory)
    {
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
    public async Task HumanAndBot_PlayPigToAWinner_ThroughTheSameRoomPlatform()
    {
        await using var me = await ConnectAsync();

        await me.SendAsync(new CreateRoom("Me", PigModule.Type, new() { ["targetScore"] = 20 }));
        var id = await me.ReceiveUntilAsync<Identity>();

        await me.SendAsync(new AddBot());
        RoomUpdate roster;
        do { roster = await me.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 2);
        Assert.Equal(PigModule.Type, roster.GameType);

        await me.SendAsync(new StartGame());

        // Policy for my seat: roll until 10 is riding, then hold. The server plays the bot.
        GameEnded? ended = null;
        var sawBotAct = false;
        for (var step = 0; step < 3000 && ended is null; step++)
        {
            switch (await me.ReceiveAsync(TimeSpan.FromSeconds(10)))
            {
                case PigTurnStarted t when t.CurrentPlayerId == id.PlayerId:
                    await me.SendAsync(new PigRoll());
                    break;
                case PigRolled r when r.PlayerId == id.PlayerId && !r.Busted:
                    await me.SendAsync(r.TurnTotal >= 10 ? new PigHold() : new PigRoll());
                    break;
                case PigRolled r when r.PlayerId != id.PlayerId:
                    sawBotAct = true;
                    break;
                case GameEnded g:
                    ended = g;
                    break;
            }
        }

        Assert.NotNull(ended);
        Assert.Contains(ended!.Players, p => p.Id == ended.WinnerId && p.Tally >= 20);
        Assert.True(sawBotAct || ended.WinnerId == id.PlayerId, "the bot should have taken turns unless I won before it moved");
    }

    [Fact]
    public async Task UnknownGameType_IsRefusedWithAnError()
    {
        await using var client = await ConnectAsync();
        await client.SendAsync(new CreateRoom("Me", "chess"));
        var error = await client.ReceiveUntilAsync<ErrorMessage>();
        Assert.Contains("chess", error.Text);
    }

    [Fact]
    public async Task Spectators_CannotMakeMoves()
    {
        await using var host = await ConnectAsync();
        await using var watcher = await ConnectAsync();

        await host.SendAsync(new CreateRoom("Host", PigModule.Type));
        var id = await host.ReceiveUntilAsync<Identity>();

        await watcher.SendAsync(new Spectate(id.RoomCode));
        await watcher.ReceiveUntilAsync<SpectateAccepted>();

        await watcher.SendAsync(new PigRoll());
        var error = await watcher.ReceiveUntilAsync<ErrorMessage>();
        Assert.Contains("watch", error.Text);
    }
}
