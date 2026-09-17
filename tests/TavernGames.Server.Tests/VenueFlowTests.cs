using TavernGames.Core;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Tests;

public class VenueFlowTests(TavernFactory factory) : IClassFixture<TavernFactory>
{
    private async Task<(WsTestClient Client, Welcome Welcome)> ConnectAsAsync(string name, string? token = null)
    {
        var client = await factory.ConnectAsync();
        await client.SendAsync(new Hello(token, name));
        return (client, await client.ReceiveUntilAsync<Welcome>());
    }

    [Fact]
    public async Task Reconnecting_WithTheIssuedToken_RestoresTheProfileAndItsVenues()
    {
        var (first, welcome) = await ConnectAsAsync("Mina");
        await first.SendAsync(new CreateVenue("The Gilded Moogle", "Dice nightly."));
        await first.ReceiveUntilAsync<VenueDetails>();
        await first.DisposeAsync();

        var (second, again) = await ConnectAsAsync("Ignored", welcome.Token);
        await using var _ = second;

        Assert.Equal(welcome.Profile.Id, again.Profile.Id);
        Assert.Equal("Mina", again.Profile.DisplayName);
        Assert.Equal("The Gilded Moogle", Assert.Single(again.Venues).Name);
    }

    [Fact]
    public async Task VenueMessages_NeedAProfile()
    {
        await using var guest = await factory.ConnectAsync();
        await guest.SendAsync(new CreateVenue("No Profile Inn", ""));
        var error = await guest.ReceiveUntilAsync<ErrorMessage>();
        Assert.Contains("profile", error.Text);
    }

    [Fact]
    public async Task OnlyStaff_HostVenueTables_AndOnlyMembers_TakeASeat()
    {
        var (owner, _) = await ConnectAsAsync("Owner");
        var (member, _) = await ConnectAsAsync("Member");
        var (outsider, _) = await ConnectAsAsync("Outsider");
        await using var d1 = owner; await using var d2 = member; await using var d3 = outsider;

        await owner.SendAsync(new CreateVenue("The Gilded Moogle", ""));
        var venue = (await owner.ReceiveUntilAsync<VenueDetails>()).Venue;
        await member.SendAsync(new JoinVenue(venue.JoinCode!));
        await member.ReceiveUntilAsync<VenueDetails>();

        // A plain member can't host for the venue.
        await member.SendAsync(new CreateRoom("Member", PigModule.Type, VenueId: venue.Id));
        Assert.Contains("staff", (await member.ReceiveUntilAsync<ErrorMessage>()).Text);

        // The owner can, and the table is announced under the venue's name.
        await owner.SendAsync(new CreateRoom("Owner", PigModule.Type, VenueId: venue.Id));
        var room = await owner.ReceiveUntilAsync<RoomUpdate>();
        Assert.Equal("The Gilded Moogle", room.VenueName);

        // It shows up in the venue's open tables for members.
        await member.SendAsync(new GetVenue(venue.Id));
        var table = Assert.Single((await member.ReceiveUntilAsync<VenueDetails>()).Venue.Tables);
        Assert.Equal((room.RoomCode, "Owner", 1, GamePhase.Lobby), (table.RoomCode, table.HostName, table.Players, table.Phase));

        // An outsider who learns the room code can watch but not sit.
        await outsider.SendAsync(new JoinRoom(room.RoomCode, "Outsider"));
        Assert.Contains("Join the venue", (await outsider.ReceiveUntilAsync<ErrorMessage>()).Text);
        await outsider.SendAsync(new Spectate(room.RoomCode));
        await outsider.ReceiveUntilAsync<SpectateAccepted>();

        // A member sits down without fuss.
        await member.SendAsync(new JoinRoom(room.RoomCode, "Member"));
        await member.ReceiveUntilAsync<Identity>();
    }

    [Fact]
    public async Task AVenueGame_BetweenTwoMembers_LandsOnTheLeaderboard_AndInTheirStats()
    {
        var (owner, ownerWelcome) = await ConnectAsAsync("Owner");
        var (member, memberWelcome) = await ConnectAsAsync("Member");
        await using var d1 = owner; await using var d2 = member;

        await owner.SendAsync(new CreateVenue("The Gilded Moogle", ""));
        var venue = (await owner.ReceiveUntilAsync<VenueDetails>()).Venue;
        await member.SendAsync(new JoinVenue(venue.JoinCode!));
        await member.ReceiveUntilAsync<VenueDetails>();

        await owner.SendAsync(new CreateRoom("Owner", PigModule.Type, new() { ["targetScore"] = 20 }, VenueId: venue.Id));
        var ownerSeat = await owner.ReceiveUntilAsync<Identity>();
        await member.SendAsync(new JoinRoom(ownerSeat.RoomCode, "Member"));
        var memberSeat = await member.ReceiveUntilAsync<Identity>();
        await owner.SendAsync(new StartGame());

        var results = await Task.WhenAll(PlayPigAsync(owner, ownerSeat.PlayerId), PlayPigAsync(member, memberSeat.PlayerId));
        var winnerSeat = results[0].WinnerId;
        var winnerProfile = winnerSeat == ownerSeat.PlayerId ? ownerWelcome.Profile.Id : memberWelcome.Profile.Id;

        await member.SendAsync(new GetLeaderboard(venue.Id, null));
        var board = (await member.ReceiveUntilAsync<VenueLeaderboard>()).Rows;
        Assert.Equal(2, board.Length);
        Assert.Equal((winnerProfile, 1, 1), (board[0].ProfileId, board[0].Played, board[0].Won));
        Assert.Equal((1, 0), (board[1].Played, board[1].Won));

        await owner.SendAsync(new GetStats());
        var pig = Assert.Single((await owner.ReceiveUntilAsync<Stats>()).Games);
        Assert.Equal((PigModule.Type, 1), (pig.GameType, pig.Played));
    }

    [Fact]
    public async Task ASoloGameAgainstBots_CountsForStats_ButNotTheLeaderboard()
    {
        var (owner, _) = await ConnectAsAsync("Owner");
        await using var d = owner;

        await owner.SendAsync(new CreateVenue("The Gilded Moogle", ""));
        var venue = (await owner.ReceiveUntilAsync<VenueDetails>()).Venue;

        await owner.SendAsync(new CreateRoom("Owner", PigModule.Type, new() { ["targetScore"] = 20 }, VenueId: venue.Id));
        var seat = await owner.ReceiveUntilAsync<Identity>();
        await owner.SendAsync(new AddBot());
        await owner.SendAsync(new StartGame());
        await PlayPigAsync(owner, seat.PlayerId);

        await owner.SendAsync(new GetLeaderboard(venue.Id, null));
        Assert.Empty((await owner.ReceiveUntilAsync<VenueLeaderboard>()).Rows);

        await owner.SendAsync(new GetStats());
        Assert.Equal(1, Assert.Single((await owner.ReceiveUntilAsync<Stats>()).Games).Played);
    }

    [Fact]
    public async Task LeavingATableAsSpectator_LetsYouSitAtTheNextOne()
    {
        var (host, _) = await ConnectAsAsync("Host");
        var (wanderer, _) = await ConnectAsAsync("Wanderer");
        await using var d1 = host; await using var d2 = wanderer;

        await host.SendAsync(new CreateRoom("Host", PigModule.Type));
        var seat = await host.ReceiveUntilAsync<Identity>();

        await wanderer.SendAsync(new Spectate(seat.RoomCode));
        await wanderer.ReceiveUntilAsync<SpectateAccepted>();
        await wanderer.SendAsync(new LeaveRoom());

        await wanderer.SendAsync(new JoinRoom(seat.RoomCode, "Wanderer"));
        await wanderer.ReceiveUntilAsync<Identity>();
        await host.SendAsync(new StartGame());

        // If the spectator flag had stuck, this roll would be refused with "Spectators can only watch."
        var first = await wanderer.ReceiveUntilAsync<PigTurnStarted>();
        if (first.CurrentPlayerId != seat.PlayerId)
        {
            await wanderer.SendAsync(new PigRoll());
            await wanderer.ReceiveUntilAsync<PigRolled>();
        }
    }

    /// <summary>Plays one seat of Pig (hold at 10) until the game ends.</summary>
    private static async Task<GameEnded> PlayPigAsync(WsTestClient client, string mySeat)
    {
        for (var step = 0; step < 5000; step++)
        {
            switch (await client.ReceiveAsync(TimeSpan.FromSeconds(15)))
            {
                case PigTurnStarted t when t.CurrentPlayerId == mySeat:
                    await client.SendAsync(new PigRoll());
                    break;
                case PigRolled r when r.PlayerId == mySeat && !r.Busted:
                    await client.SendAsync(r.TurnTotal >= 10 ? new PigHold() : new PigRoll());
                    break;
                case GameEnded ended:
                    return ended;
            }
        }
        throw new TimeoutException("The Pig game never ended.");
    }
}
