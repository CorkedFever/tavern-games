using Microsoft.AspNetCore.Mvc.Testing;
using TavernGames.Core.Games.LiarsDice;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class GameFlowTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private async Task<WsTestClient> ConnectAsync()
    {
        var http = new Uri(factory.Server.BaseAddress, "ws");
        var wsUri = new UriBuilder(http) { Scheme = "ws" }.Uri;
        var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(wsUri, CancellationToken.None);
        return new WsTestClient(ws);
    }

    [Fact]
    public async Task TwoPlayers_PlayAFullOneDieGame_ToCompletion()
    {
        await using var host = await ConnectAsync();
        await using var guest = await ConnectAsync();

        // Host creates a single-die room.
        await host.SendAsync(new CreateRoom("Host", LiarsDiceModule.Type, new() { ["startingDice"] = 1 }));
        var hostId = (await host.ReceiveUntilAsync<Identity>());
        var roomCode = hostId.RoomCode;

        // Guest joins by code.
        await guest.SendAsync(new JoinRoom(roomCode, "Guest"));
        var guestId = await guest.ReceiveUntilAsync<Identity>();

        Assert.Equal(roomCode, guestId.RoomCode);
        Assert.Equal(hostId.PlayerId, hostId.HostId); // host is the room host

        // Host starts; the round begins for both.
        await host.SendAsync(new StartGame());
        var round = await host.ReceiveUntilAsync<RoundStarted>();
        await guest.ReceiveUntilAsync<RoundStarted>();

        Assert.Equal(2, round.Players.Length);
        Assert.All(round.Players, p => Assert.Equal(1, p.Tally));

        // Identify the player to act and their opponent.
        var current = round.CurrentPlayerId == hostId.PlayerId ? host : guest;
        var other = round.CurrentPlayerId == hostId.PlayerId ? guest : host;

        // Current player makes an opening bid; the opponent immediately calls liar.
        await current.SendAsync(new PlaceBid(Quantity: 2, FaceValue: 6));
        await other.ReceiveUntilAsync<BidPlaced>();
        await other.SendAsync(new Challenge());

        // With only two dice on the table, "two 6s" usually fails — but win or lose,
        // a single die is lost and the game must end with a winner.
        var resolved = await host.ReceiveUntilAsync<ChallengeResolved>();
        Assert.True(resolved.GameOver);

        var ended = await host.ReceiveUntilAsync<GameEnded>();
        Assert.Contains(ended.WinnerId, new[] { hostId.PlayerId, guestId.PlayerId });
    }

    [Fact]
    public async Task JoiningUnknownRoom_ReturnsError()
    {
        await using var client = await ConnectAsync();
        await client.SendAsync(new JoinRoom("ZZZZ", "Nobody"));
        var error = await client.ReceiveUntilAsync<ErrorMessage>();
        Assert.Contains("ZZZZ", error.Text);
    }
}
