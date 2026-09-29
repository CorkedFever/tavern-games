using TavernGames.Core.Games.Roulette;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class RouletteFlowTests(TavernFactory factory) : IClassFixture<TavernFactory>
{
    [Fact]
    public async Task AHumanAndABot_PlayRouletteToTheLastSpin()
    {
        await using var me = await factory.ConnectAsync();

        await me.SendAsync(new CreateRoom("Me", RouletteModule.Type, new() { ["rounds"] = 3, ["startingChips"] = 200 }));
        var id = await me.ReceiveUntilAsync<Identity>();
        await me.SendAsync(new AddBot());
        await me.SendAsync(new StartGame());

        GameEnded? ended = null;
        var settled = 0;
        var sawBotBet = false;
        var spins = new List<int>();

        for (var step = 0; step < 2000 && ended is null; step++)
        {
            var message = await me.ReceiveAsync(TimeSpan.FromSeconds(15));
            switch (message)
            {
                case RouletteBettingOpened m when m.Table.Seats.Any(s => s.PlayerId == id.PlayerId && !s.Out):
                    // My system: the minimum on red, every spin.
                    await me.SendAsync(new RoulettePlace(RouletteBetKind.Red, 0, m.Table.MinBet));
                    await me.SendAsync(new RouletteDone());
                    break;
                case RouletteBetPlaced m when m.PlayerId != id.PlayerId:
                    sawBotBet = true;
                    break;
                case RouletteSpun m:
                    Assert.InRange(m.Number, 0, 36);
                    Assert.False(m.Table.Betting);
                    Assert.NotEmpty(m.Table.Seats.Single(s => s.PlayerId == id.PlayerId).Bets); // still on the layout while the ball rolls
                    spins.Add(m.Number);
                    break;
                case RouletteSettled m:
                    Assert.Equal(spins[^1], m.Number);
                    Assert.Contains(m.Payouts, p => p.PlayerId == id.PlayerId && p.Staked == 5);
                    settled++;
                    break;
                case GameEnded g:
                    ended = g;
                    break;
            }
        }

        Assert.NotNull(ended);
        Assert.Equal(3, settled);
        Assert.True(sawBotBet);
        Assert.Contains(ended!.Players, p => p.Id == ended.WinnerId);
    }

    [Fact]
    public async Task Roulette_CanBePlayedAlone_AgainstTheHouse()
    {
        await using var me = await factory.ConnectAsync();
        await me.SendAsync(new CreateRoom("Me", RouletteModule.Type, new() { ["rounds"] = 3 }));
        await me.ReceiveUntilAsync<Identity>();

        await me.SendAsync(new StartGame()); // no second player, no bot

        var opened = await me.ReceiveUntilAsync<RouletteBettingOpened>();
        Assert.Single(opened.Table.Seats);
        Assert.True(opened.Table.Betting);
    }
}
