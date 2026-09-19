using TavernGames.Core.Games.Blackjack;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Tests;

/// <summary>
/// The offline path the plugin uses to play against bots with no server. These drive the
/// same SoloTable the plugin drives, with instant pacing, and read only the delivered
/// messages, the way the client does.
/// </summary>
public class SoloTableTests
{
    private sealed class Client
    {
        public List<NetMessage> Messages { get; } = new();
        public void Deliver(NetMessage m) { lock (Messages) Messages.Add(m); }
        public IEnumerable<T> OfKind<T>() where T : NetMessage { lock (Messages) return Messages.OfType<T>().ToList(); }
        public bool GameEnded => OfKind<GameEnded>().Any();
    }

    private static SoloTable New(Client client, string game, int bots, out Task started,
        Dictionary<string, int>? options = null, int seed = 1)
    {
        // turnDelayMs 0 makes every bot turn and pause resolve synchronously, so awaiting a
        // submit means the table has run as far as it can without the human.
        var table = new SoloTable(game, options, turnDelayMs: 0, "You", client.Deliver, rng: new Random(seed));
        started = table.QuickStartAsync(bots);
        return table;
    }

    [Fact]
    public async Task EveryGame_DealsAndStartsFromASingleClick()
    {
        foreach (var game in GameCatalog.Games)
        {
            var client = new Client();
            var table = New(client, game.Type, bots: game.MaxPlayers - 1, out var started);
            await started;

            // The client is put straight into a room and the game is under way.
            Assert.Equal(SoloTable.HumanId, Assert.IsType<Identity>(client.Messages[0]).PlayerId);
            var lastRoom = client.OfKind<RoomUpdate>().Last();
            Assert.Equal(game.Type, lastRoom.GameType);
            Assert.Equal(GamePhase.Playing, lastRoom.Phase);

            // Either it is now the human's turn, or a bot is on the clock and the game is live,
            // or (short game) it already finished. It never dead-ends in the lobby.
            Assert.True(client.GameEnded || client.Messages.Count > 2, $"{game.Type} produced no play");
            table.Dispose();
        }
    }

    [Fact]
    public async Task Pig_PlaysToAWinner_AgainstBots()
    {
        var client = new Client();
        var table = New(client, PigModule.Type, bots: 2, out var started, new() { ["targetScore"] = 40 });
        await started;

        for (var step = 0; step < 5000 && !client.GameEnded; step++)
        {
            var turn = client.OfKind<PigTurnStarted>().LastOrDefault();
            var rolled = client.OfKind<PigRolled>().LastOrDefault();
            if (turn?.CurrentPlayerId != SoloTable.HumanId) break; // not our turn and no bot moved: shouldn't happen

            var mine = rolled is { PlayerId: SoloTable.HumanId, Busted: false } ? rolled.TurnTotal : 0;
            await table.SubmitAsync(mine >= 20 ? new PigHold() : new PigRoll());
        }

        Assert.True(client.GameEnded);
        var ended = client.OfKind<GameEnded>().Single();
        Assert.Contains(ended.Players, p => p.Id == ended.WinnerId);
    }

    [Fact]
    public async Task Blackjack_CanBePlayedSoloAgainstTheDealer_NoBots()
    {
        var client = new Client();
        var table = New(client, BlackjackModule.Type, bots: 0, out var started, new() { ["rounds"] = 3, ["minBet"] = 10 });
        await started;

        for (var step = 0; step < 200 && !client.GameEnded; step++)
        {
            var table2 = LatestTable(client);
            if (table2 is null) break;

            if (table2.Betting && table2.Seats.Any(s => s.PlayerId == SoloTable.HumanId && s.Bet == 0 && !s.Out))
                await table.SubmitAsync(new BjBet(table2.MinBet));
            else if (table2.CurrentPlayerId == SoloTable.HumanId)
                await table.SubmitAsync(new BjStand());
            else
                break; // nothing for the human to do and no bots: the dealer/settlement already ran synchronously
        }

        Assert.True(client.GameEnded);
        Assert.Equal(SoloTable.HumanId, client.OfKind<GameEnded>().Single().WinnerId); // solo: the human is the only seat
    }

    [Fact]
    public async Task LeavingIsClean_AndTheBotLoopStops()
    {
        var client = new Client();
        var table = New(client, PigModule.Type, bots: 3, out var started);
        await started;
        table.Dispose();

        var countAfterDispose = client.Messages.Count;
        // A late move after disposal does nothing and cannot throw.
        await table.SubmitAsync(new PigRoll());
        Assert.Equal(countAfterDispose, client.Messages.Count);
    }

    private static BjTable? LatestTable(Client client)
    {
        lock (client.Messages)
            return client.Messages.Select(m => m switch
            {
                BjBettingOpened x => x.Table,
                BjBetPlaced x => x.Table,
                BjDealt x => x.Table,
                BjPlayed x => x.Table,
                BjDealerPlayed x => x.Table,
                BjRoundSettled x => x.Table,
                _ => null,
            }).LastOrDefault(t => t is not null);
    }
}
