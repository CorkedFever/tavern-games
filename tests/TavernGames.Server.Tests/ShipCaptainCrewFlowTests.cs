using TavernGames.Core.Games.ShipCaptainCrew;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class ShipCaptainCrewFlowTests(TavernFactory factory) : IClassFixture<TavernFactory>
{
    [Fact]
    public async Task AHumanAndABot_PlayToTheLastRound()
    {
        await using var me = await factory.ConnectAsync();

        await me.SendAsync(new CreateRoom("Me", ShipCaptainCrewModule.Type, new() { ["roundsToWin"] = 2 }));
        var id = await me.ReceiveUntilAsync<Identity>();
        await me.SendAsync(new AddBot());
        await me.SendAsync(new StartGame());

        GameEnded? ended = null;
        var roundsWon = 0;
        var myTurns = 0;
        var sawBotRoll = false;

        for (var step = 0; step < 3000 && ended is null; step++)
        {
            var message = await me.ReceiveAsync(TimeSpan.FromSeconds(15));
            switch (message)
            {
                case SccTurnStarted m when m.PlayerId == id.PlayerId:
                    myTurns++;
                    await me.SendAsync(new SccRoll());
                    break;

                case SccRolled m when m.PlayerId == id.PlayerId:
                {
                    // My policy: roll until the crew is aboard, then hold whatever the cargo is.
                    var seat = m.Table.Seats.Single(s => s.PlayerId == id.PlayerId);
                    Assert.Equal(5, m.Dice.Length);
                    Assert.All(m.Dice, d => Assert.InRange(d, 1, 6));
                    if (m.RollsLeft > 0)
                        await me.SendAsync(seat.Crew ? new SccHold() : new SccRoll());
                    break;
                }

                case SccRolled:
                    sawBotRoll = true;
                    break;

                case SccRoundEnded m when m.WinnerId is not null:
                    roundsWon++;
                    break;

                case GameEnded g:
                    ended = g;
                    break;
            }
        }

        Assert.NotNull(ended);
        Assert.True(roundsWon >= 2);
        Assert.True(myTurns >= 2);
        Assert.True(sawBotRoll);
        Assert.Contains(ended!.Players, p => p.Id == ended.WinnerId);
    }
}
