using TavernGames.Core.Cards;
using TavernGames.Core.Games.Holdem;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class HoldemFlowTests(TavernFactory factory) : IClassFixture<TavernFactory>
{
    /// <summary>
    /// A seat that only ever plays what the public table says is legal: check when it is
    /// free, call while the price is small, and give up otherwise. If the table did not carry
    /// enough for a client to act on, this would start drawing errors from the server.
    /// </summary>
    private static HoldemAct Decide(HoldemTable table, HoldemSeat mine) =>
        table.ToCall == 0 ? new HoldemAct(HoldemMove.Check)
        : table.ToCall <= Math.Max(table.BigBlind, mine.Chips / 4) ? new HoldemAct(HoldemMove.Call)
        : new HoldemAct(HoldemMove.Fold);

    private static HoldemTable? TableOf(NetMessage? message) => message switch
    {
        HoldemHandStarted m => m.Table,
        HoldemBlindPosted m => m.Table,
        HoldemActed m => m.Table,
        HoldemStreetDealt m => m.Table,
        HoldemShowdown m => m.Table,
        HoldemHandEnded m => m.Table,
        HoldemSnapshot m => m.Table,
        _ => null,
    };

    private static int BoardSizeFor(HoldemStreet street) => street switch
    {
        HoldemStreet.Preflop => 0,
        HoldemStreet.Flop => 3,
        HoldemStreet.Turn => 4,
        _ => 5,
    };

    [Fact]
    public async Task AHumanAndTwoBots_PlayHoldemToAWinner_ThroughTheRealServer()
    {
        await using var me = await factory.ConnectAsync();

        await me.SendAsync(new CreateRoom("Me", HoldemModule.Type, new()
        {
            ["startingChips"] = 500, // the option slider floor; 3 seats means 1500 chips in play
            ["smallBlind"] = 25,
            ["blindsUpEvery"] = 2, // rising blinds keep a bot-heavy table short
        }));
        var id = await me.ReceiveUntilAsync<Identity>();

        await me.SendAsync(new AddBot());
        await me.SendAsync(new AddBot());
        RoomUpdate roster;
        do { roster = await me.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 3);
        Assert.Equal(HoldemModule.Type, roster.GameType);

        await me.SendAsync(new StartGame());

        GameEnded? ended = null;
        var handsDealt = 0;
        var sawMyOwnCards = false;
        var sawABotAct = false;
        var myTurnIsPending = false;

        for (var step = 0; step < 20_000 && ended is null; step++)
        {
            var message = await me.ReceiveAsync(TimeSpan.FromSeconds(30));

            switch (message)
            {
                case HoldemYourCards cards:
                    // The private deal, and the only place a real card code should reach me.
                    Assert.Equal(2, cards.Cards.Length);
                    Assert.DoesNotContain(Card.HiddenCode, cards.Cards);
                    sawMyOwnCards = true;
                    continue;

                case ErrorMessage error:
                    Assert.Fail($"the server refused a move built from the public table: {error.Text}");
                    break;

                case HoldemHandStarted:
                    handsDealt++;
                    break;

                case HoldemActed acted when acted.PlayerId != id.PlayerId:
                    sawABotAct = true;
                    break;

                case GameEnded game:
                    ended = game;
                    continue;
            }

            if (TableOf(message) is not { } table) continue;

            Assert.Equal(BoardSizeFor(table.Street), table.Community.Length);

            if (table.CurrentPlayerId != id.PlayerId)
            {
                myTurnIsPending = false;
                continue;
            }

            // Several messages can carry the same "your turn" table; only answer the first.
            if (myTurnIsPending) continue;
            myTurnIsPending = true;
            await me.SendAsync(Decide(table, table.Seats.Single(s => s.PlayerId == id.PlayerId)));
        }

        Assert.NotNull(ended);
        Assert.True(sawMyOwnCards, "my own hole cards should have been dealt privately");
        Assert.True(sawABotAct, "the bots should have taken their turns");
        Assert.True(handsDealt >= 2, $"the tournament should have run more than one hand, saw {handsDealt}");

        // One seat holds every chip and everybody else is knocked out.
        var winner = Assert.Single(ended!.Players, p => p.Id == ended.WinnerId);
        Assert.False(winner.Eliminated);
        Assert.Equal(ended.Players.Sum(p => p.Tally), winner.Tally); // the winner holds every chip
        Assert.All(ended.Players.Where(p => p.Id != winner.Id), p => Assert.True(p.Eliminated));
    }

    [Fact]
    public async Task ALateSpectator_IsCaughtUpToTheExactPublicTable_AndNeverSeesAHand()
    {
        await using var host = await factory.ConnectAsync();

        await host.SendAsync(new CreateRoom("Host", HoldemModule.Type, new()
        {
            ["startingChips"] = 2000,
            ["smallBlind"] = 10,
            ["blindsUpEvery"] = 0, // a long, slow table: it has to still be running when the watcher arrives
        }));
        var id = await host.ReceiveUntilAsync<Identity>();

        await host.SendAsync(new AddBot());
        await host.SendAsync(new AddBot());
        RoomUpdate roster;
        do { roster = await host.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 3);

        await host.SendAsync(new StartGame());

        // Play on until the board is out and the table is waiting on me. A table on my clock
        // that I have not answered yet is the live one by definition: the server cannot deal
        // another card or move a bot until my move arrives, so nothing is queued behind it.
        HoldemTable? live = null;
        var myTurnIsPending = false;
        for (var step = 0; step < 5000; step++)
        {
            var message = await host.ReceiveAsync(TimeSpan.FromSeconds(30));
            Assert.IsNotType<ErrorMessage>(message);
            if (TableOf(message) is not { } table) continue;

            live = table;
            if (table.CurrentPlayerId != id.PlayerId)
            {
                myTurnIsPending = false;
                continue;
            }

            if (myTurnIsPending) continue; // an echo of a turn I have already answered
            if (table.Street != HoldemStreet.Preflop) break;

            myTurnIsPending = true;
            await host.SendAsync(Decide(table, table.Seats.Single(s => s.PlayerId == id.PlayerId)));
        }

        Assert.NotNull(live);
        Assert.NotEqual(HoldemStreet.Preflop, live!.Street);

        await using var watcher = await factory.ConnectAsync();
        await watcher.SendAsync(new Spectate(id.RoomCode));
        Assert.Equal(id.RoomCode, (await watcher.ReceiveUntilAsync<SpectateAccepted>()).RoomCode);
        await watcher.ReceiveUntilAsync<RoomUpdate>();

        var caughtUp = (await watcher.ReceiveUntilAsync<HoldemSnapshot>()).Table;

        Assert.Equal(
            (live.Hand, live.Street, live.Pot, live.CurrentPlayerId, live.ButtonId, live.CurrentBet, live.BigBlind),
            (caughtUp.Hand, caughtUp.Street, caughtUp.Pot, caughtUp.CurrentPlayerId, caughtUp.ButtonId,
                caughtUp.CurrentBet, caughtUp.BigBlind));
        Assert.Equal(live.Community, caughtUp.Community);
        Assert.Equal(
            live.Seats.Select(s => (s.PlayerId, s.Chips, s.StreetBet, s.Folded, s.AllIn)),
            caughtUp.Seats.Select(s => (s.PlayerId, s.Chips, s.StreetBet, s.Folded, s.AllIn)));

        // Nothing face up: this hand has not reached a showdown.
        Assert.All(caughtUp.Seats, s => Assert.All(s.Cards, c => Assert.Equal(Card.HiddenCode, c)));

        // And the watcher keeps receiving the public story as it happens, hand never included.
        await host.SendAsync(Decide(live, live.Seats.Single(s => s.PlayerId == id.PlayerId)));
        for (var step = 0; step < 200; step++)
        {
            var message = await watcher.ReceiveAsync(TimeSpan.FromSeconds(30));
            Assert.IsNotType<HoldemYourCards>(message);
            if (message is HoldemActed acted && acted.PlayerId == id.PlayerId) return;
        }

        Assert.Fail("the spectator should have been told about the host's action");
    }

    [Fact]
    public async Task APlayerWalkingOutMidHand_DoesNotStallTheTable()
    {
        await using var host = await factory.ConnectAsync();
        var quitter = await factory.ConnectAsync();

        await host.SendAsync(new CreateRoom("Host", HoldemModule.Type, new()
        {
            ["startingChips"] = 500,
            ["smallBlind"] = 25,
            ["blindsUpEvery"] = 2,
        }));
        var id = await host.ReceiveUntilAsync<Identity>();

        await quitter.SendAsync(new JoinRoom(id.RoomCode, "Pat"));
        await quitter.ReceiveUntilAsync<Identity>();
        await host.SendAsync(new AddBot());
        RoomUpdate roster;
        do { roster = await host.ReceiveUntilAsync<RoomUpdate>(); } while (roster.Players.Length < 3);

        await host.SendAsync(new StartGame());
        await host.ReceiveUntilAsync<HoldemBlindPosted>(); // the hand is under way

        await quitter.DisposeAsync(); // Pat closes the window mid-hand

        GameEnded? ended = null;
        var myTurnIsPending = false;
        for (var step = 0; step < 20_000 && ended is null; step++)
        {
            var message = await host.ReceiveAsync(TimeSpan.FromSeconds(30));
            if (message is GameEnded game) { ended = game; break; }
            if (TableOf(message) is not { } table) continue;

            if (table.CurrentPlayerId != id.PlayerId)
            {
                myTurnIsPending = false;
                continue;
            }
            if (myTurnIsPending) continue;
            myTurnIsPending = true;
            await host.SendAsync(Decide(table, table.Seats.Single(s => s.PlayerId == id.PlayerId)));
        }

        Assert.NotNull(ended);
        Assert.DoesNotContain(ended!.Players, p => p.Name == "Pat"); // the empty chair is gone
        Assert.Contains(ended.Players, p => p.Id == ended.WinnerId);
    }
}
