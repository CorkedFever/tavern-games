using TavernGames.Core.Cards;
using TavernGames.Core.Games.Blackjack;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class BlackjackFlowTests(TavernFactory factory) : IClassFixture<TavernFactory>
{
    [Fact]
    public async Task AHumanAndABot_PlayBlackjackToTheLastRound_AndTheHoleCardNeverLeaks()
    {
        await using var me = await factory.ConnectAsync();

        await me.SendAsync(new CreateRoom("Me", BlackjackModule.Type, new() { ["rounds"] = 3, ["startingChips"] = 200 }));
        var id = await me.ReceiveUntilAsync<Identity>();
        await me.SendAsync(new AddBot());
        await me.SendAsync(new StartGame());

        GameEnded? ended = null;
        var roundsSettled = 0;
        var sawBotBet = false;

        for (var step = 0; step < 2000 && ended is null; step++)
        {
            var message = await me.ReceiveAsync(TimeSpan.FromSeconds(15));
            var table = message switch
            {
                BjBettingOpened m => m.Table,
                BjBetPlaced m => m.Table,
                BjDealt m => m.Table,
                BjPlayed m => m.Table,
                BjDealerPlayed m => m.Table,
                BjRoundSettled m => m.Table,
                _ => null,
            };

            // While any hand is still being played, the dealer's second card must be hidden.
            if (table is { CurrentPlayerId: not null, DealerCards.Length: 2 })
                Assert.Equal(Card.HiddenCode, table.DealerCards[1]);

            switch (message)
            {
                case BjBettingOpened m when m.Table.Seats.Any(s => s.PlayerId == id.PlayerId && !s.Out):
                    await me.SendAsync(new BjBet(m.Table.MinBet));
                    break;
                case BjBetPlaced m when m.PlayerId != id.PlayerId:
                    sawBotBet = true;
                    break;
                case BjRoundSettled:
                    roundsSettled++;
                    break;
                case GameEnded g:
                    ended = g;
                    break;
            }

            // My policy: stand on whatever I'm dealt.
            if (message is BjDealt or BjPlayed && table is { Betting: false } && table.CurrentPlayerId == id.PlayerId)
                await me.SendAsync(new BjStand());
        }

        Assert.NotNull(ended);
        Assert.Equal(3, roundsSettled);
        Assert.True(sawBotBet);
        Assert.Contains(ended!.Players, p => p.Id == ended.WinnerId);
    }

    [Fact]
    public async Task Blackjack_CanBePlayedAlone_AgainstTheHouse()
    {
        await using var me = await factory.ConnectAsync();
        await me.SendAsync(new CreateRoom("Me", BlackjackModule.Type, new() { ["rounds"] = 3 }));
        await me.ReceiveUntilAsync<Identity>();

        await me.SendAsync(new StartGame()); // no second player, no bot

        var opened = await me.ReceiveUntilAsync<BjBettingOpened>();
        Assert.Single(opened.Table.Seats);
    }
}
