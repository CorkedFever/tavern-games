using TavernGames.Core.Games.Mia;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class MiaFlowTests(TavernFactory factory) : IClassFixture<TavernFactory>
{
    [Fact]
    public async Task AHumanAndTwoBots_PlayMiaToAWinner_AndOnlyMyOwnDiceEverReachMe()
    {
        await using var me = await factory.ConnectAsync();

        await me.SendAsync(new CreateRoom("Me", MiaModule.Type, new() { ["lives"] = 2 }));
        var id = await me.ReceiveUntilAsync<Identity>();
        await me.SendAsync(new AddBot());
        await me.SendAsync(new AddBot());
        RoomUpdate roster;
        do { roster = await me.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 3);

        await me.SendAsync(new StartGame());

        var myRoll = 0;
        var privateRollPending = false;
        var calls = 0;
        GameEnded? ended = null;

        for (var step = 0; step < 4000 && ended is null; step++)
        {
            var message = await me.ReceiveAsync(TimeSpan.FromSeconds(20));
            switch (message)
            {
                case MiaYourRoll roll:
                    myRoll = roll.Value;
                    privateRollPending = true;
                    break;

                case MiaRolled rolled:
                    // Dice reach me only for my own cup, and always just before its public shake.
                    if (privateRollPending) Assert.Equal(id.PlayerId, rolled.PlayerId);
                    else Assert.NotEqual(id.PlayerId, rolled.PlayerId);
                    privateRollPending = false;

                    if (rolled.Table is { Step: MiaStep.Announce } table && table.CurrentPlayerId == id.PlayerId)
                        await me.SendAsync(new MiaAnnounce(Claim(myRoll, table.Accepted)));
                    break;

                case MiaAnnounced announced when announced.Table.CurrentPlayerId == id.PlayerId:
                    await me.SendAsync(new MiaCallLiar()); // my policy: doubt everything
                    break;

                case MiaCalled:
                    calls++;
                    break;

                case GameEnded over:
                    ended = over;
                    break;
            }
        }

        Assert.NotNull(ended);
        Assert.Contains(ended!.Players, p => p.Id == ended.WinnerId && p.Tally > 0);
        Assert.Equal(1, ended.Players.Count(p => !p.Eliminated)); // exactly one player left standing
        Assert.True(calls > 0, "with that policy somebody should have been called");
    }

    [Fact]
    public async Task ALateSpectator_IsCaughtUpToThePublicTable_AndNeverSeesUnderTheCup()
    {
        await using var host = await factory.ConnectAsync();

        await host.SendAsync(new CreateRoom("Host", MiaModule.Type));
        var id = await host.ReceiveUntilAsync<Identity>();
        await host.SendAsync(new AddBot());
        RoomUpdate roster;
        do { roster = await host.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 2);

        await host.SendAsync(new StartGame());

        // The host holds the first cup, so the table is now waiting on a human: nothing
        // moves until they announce, which makes this a settled moment to look in on.
        var rolled = await host.ReceiveUntilAsync<MiaRolled>();
        Assert.Equal(id.PlayerId, rolled.PlayerId);

        await using var watcher = await factory.ConnectAsync();
        await watcher.SendAsync(new Spectate(id.RoomCode));
        await watcher.ReceiveUntilAsync<SpectateAccepted>();
        var snapshot = await watcher.ReceiveUntilAsync<MiaSnapshot>();

        // Catch-up is the very table the seated players are looking at, to the field.
        Assert.Equal(new MiaSnapshot(rolled.Table).Serialize(), snapshot.Serialize());
        Assert.Equal((MiaStep.Announce, id.PlayerId, 0, 0),
            (snapshot.Table.Step, snapshot.Table.CurrentPlayerId, snapshot.Table.Announced, snapshot.Table.Accepted));
        Assert.Equal([MiaGame.DefaultLives, MiaGame.DefaultLives], snapshot.Table.Seats.Select(s => s.Lives));

        await host.SendAsync(new MiaAnnounce(MiaValue.Mia.Code));

        var seen = new List<NetMessage?>();
        for (var step = 0; step < 50; step++)
        {
            var message = await watcher.ReceiveAsync(TimeSpan.FromSeconds(15));
            Assert.IsNotType<MiaYourRoll>(message); // the whole point: watchers never see a cup
            seen.Add(message);
            if (message is MiaCalled or MiaConceded) break;
        }

        Assert.Contains(seen, m => m is MiaAnnounced { Value: 21 });
        Assert.Contains(seen, m => m is MiaCalled or MiaConceded); // the bot cannot believe a Mia
    }

    [Fact]
    public async Task WhenThePlayerOnTheClockWalksOut_TheTableCarriesOnWithoutThem()
    {
        await using var watcher = await factory.ConnectAsync();
        await using var me = await factory.ConnectAsync();

        await me.SendAsync(new CreateRoom("Me", MiaModule.Type, new() { ["lives"] = 1 }));
        var id = await me.ReceiveUntilAsync<Identity>();
        await me.SendAsync(new AddBot());
        await me.SendAsync(new AddBot());
        RoomUpdate roster;
        do { roster = await me.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 3);

        await me.SendAsync(new StartGame());
        await me.ReceiveUntilAsync<MiaRolled>(); // the table is waiting on me

        await watcher.SendAsync(new Spectate(id.RoomCode));
        await watcher.ReceiveUntilAsync<MiaSnapshot>();

        await me.SendAsync(new LeaveRoom()); // and I walk out holding the cup

        var ended = await watcher.ReceiveUntilAsync<GameEnded>(TimeSpan.FromSeconds(30));

        Assert.Equal(2, ended.Players.Length); // the two bots played it out between them
        Assert.DoesNotContain(ended.Players, p => p.Id == id.PlayerId);
        Assert.Contains(ended.Players, p => p.Id == ended.WinnerId && p.Tally > 0);
    }

    /// <summary>A seated player's policy: the truth while it is legal, else the smallest lie that passes.</summary>
    private static int Claim(int roll, int accepted)
    {
        MiaValue? floor = MiaValue.TryFromCode(accepted, out var value) ? value : null;
        if (MiaValue.TryFromCode(roll, out var truth) && (floor is not { } beat || truth.Beats(beat)))
            return truth.Code;
        return MiaValue.Ordered.First(v => floor is not { } lowest || v.Beats(lowest)).Code;
    }
}
