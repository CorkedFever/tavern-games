using TavernGames.Core.Protocol;
using TavernGames.Server.Data;

namespace TavernGames.Server.Tests;

/// <summary>The rules about who may do what, tested directly against a throwaway database.</summary>
public sealed class CommunityStoreTests : IDisposable
{
    private readonly string _path = TavernFactory.NewDbPath();
    private readonly CommunityStore _store;

    public CommunityStoreTests() => _store = new CommunityStore(new TavernDb(_path));

    public void Dispose() => TavernFactory.DeleteDb(_path);

    private string NewProfile(string name) => _store.Identify(null, name).Profile.Id;

    [Fact]
    public void Identify_ReturnsTheSameProfile_ForTheSameToken()
    {
        var (first, token) = _store.Identify(null, "Mina");
        var (again, sameToken) = _store.Identify(token, "Somebody Else");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Mina", again.DisplayName); // a returning player keeps their name
        Assert.Equal(token, sameToken);
    }

    [Fact]
    public void Identify_CreatesAFreshProfile_ForAnUnknownToken()
    {
        var (first, _) = _store.Identify(null, "Mina");
        var (other, token) = _store.Identify("not-a-real-token", "Mina");

        Assert.NotEqual(first.Id, other.Id);
        Assert.NotEqual("not-a-real-token", token);
    }

    [Fact]
    public void Tokens_AreStoredHashed_NeverInTheClear()
    {
        var (_, token) = _store.Identify(null, "Mina");

        using var connection = new TavernDb(_path).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM profiles";
        var stored = (string)command.ExecuteScalar()!;
        Assert.NotEqual(token, stored);
        Assert.DoesNotContain(token, stored);
    }

    [Fact]
    public void Names_AreTrimmedCappedAndStrippedOfControlCharacters()
    {
        var profile = _store.UpdateProfile(NewProfile("x"), "  Mi\nna\t" + new string('z', 40), "the" + (char)7 + " masked gambler"); // (char)7 = a bell control character

        Assert.Equal(24, profile.DisplayName.Length);
        Assert.StartsWith("Mina", profile.DisplayName);
        Assert.Equal("the masked gambler", profile.Tagline);
    }

    [Fact]
    public void JoiningByCode_MakesYouAMember_AndHidesTheCodeFromYou()
    {
        var owner = NewProfile("Owner");
        var guest = NewProfile("Guest");
        var venue = _store.CreateVenue(owner, "The Gilded Moogle", "Dice nightly.");
        var code = _store.GetVenue(owner, venue, []).JoinCode!;

        _store.JoinVenue(guest, code.ToLowerInvariant()); // codes are case-insensitive

        var seenByGuest = _store.GetVenue(guest, venue, []);
        Assert.Equal(VenueRole.Member, seenByGuest.MyRole);
        Assert.Null(seenByGuest.JoinCode);
        Assert.Equal(2, seenByGuest.Members.Length);
        Assert.Equal(VenueRole.Owner, seenByGuest.Members[0].Role); // owner listed first
    }

    [Fact]
    public void JoiningTwice_IsHarmless()
    {
        var owner = NewProfile("Owner");
        var guest = NewProfile("Guest");
        var venue = _store.CreateVenue(owner, "The Gilded Moogle", "");
        var code = _store.GetVenue(owner, venue, []).JoinCode!;

        _store.JoinVenue(guest, code);
        _store.JoinVenue(guest, code);

        Assert.Equal(2, _store.GetVenue(owner, venue, []).Members.Length);
    }

    [Fact]
    public void Outsiders_CannotReadAVenueOrItsLeaderboard()
    {
        var venue = _store.CreateVenue(NewProfile("Owner"), "The Gilded Moogle", "");
        var outsider = NewProfile("Outsider");

        Assert.Throws<InvalidOperationException>(() => _store.GetVenue(outsider, venue, []));
        Assert.Throws<InvalidOperationException>(() => _store.Leaderboard(outsider, venue, null));
    }

    [Fact]
    public void RegeneratingTheCode_KillsTheOldOne()
    {
        var owner = NewProfile("Owner");
        var venue = _store.CreateVenue(owner, "The Gilded Moogle", "");
        var oldCode = _store.GetVenue(owner, venue, []).JoinCode!;

        _store.RegenerateCode(owner, venue);

        Assert.NotEqual(oldCode, _store.GetVenue(owner, venue, []).JoinCode);
        Assert.Throws<InvalidOperationException>(() => _store.JoinVenue(NewProfile("Late"), oldCode));
    }

    [Fact]
    public void OnlyTheOwner_ChangesRoles_AndMembersCannotManageAnything()
    {
        var owner = NewProfile("Owner");
        var member = NewProfile("Member");
        var other = NewProfile("Other");
        var venue = _store.CreateVenue(owner, "The Gilded Moogle", "");
        var code = _store.GetVenue(owner, venue, []).JoinCode!;
        _store.JoinVenue(member, code);
        _store.JoinVenue(other, code);

        Assert.Throws<InvalidOperationException>(() => _store.SetRole(member, venue, other, VenueRole.Staff));
        Assert.Throws<InvalidOperationException>(() => _store.Kick(member, venue, other));
        Assert.Throws<InvalidOperationException>(() => _store.RegenerateCode(member, venue));
        Assert.Throws<InvalidOperationException>(() => _store.UpdateVenue(member, venue, "Mine Now", ""));
        Assert.Throws<InvalidOperationException>(() => _store.DeleteVenue(member, venue));

        _store.SetRole(owner, venue, member, VenueRole.Staff);
        Assert.Equal(VenueRole.Staff, _store.RoleOf(member, venue));
    }

    [Fact]
    public void Staff_CanRemoveMembers_ButNotEachOtherOrTheOwner()
    {
        var owner = NewProfile("Owner");
        var staffA = NewProfile("StaffA");
        var staffB = NewProfile("StaffB");
        var member = NewProfile("Member");
        var venue = _store.CreateVenue(owner, "The Gilded Moogle", "");
        var code = _store.GetVenue(owner, venue, []).JoinCode!;
        foreach (var id in new[] { staffA, staffB, member }) _store.JoinVenue(id, code);
        _store.SetRole(owner, venue, staffA, VenueRole.Staff);
        _store.SetRole(owner, venue, staffB, VenueRole.Staff);

        _store.Kick(staffA, venue, member);
        Assert.Null(_store.RoleOf(member, venue));

        Assert.Throws<InvalidOperationException>(() => _store.Kick(staffA, venue, staffB));
        Assert.Throws<InvalidOperationException>(() => _store.Kick(staffA, venue, owner));
        Assert.Throws<InvalidOperationException>(() => _store.Kick(owner, venue, owner));
    }

    [Fact]
    public void HandingOverAVenue_LeavesExactlyOneOwner()
    {
        var owner = NewProfile("Owner");
        var heir = NewProfile("Heir");
        var venue = _store.CreateVenue(owner, "The Gilded Moogle", "");
        _store.JoinVenue(heir, _store.GetVenue(owner, venue, []).JoinCode!);

        _store.SetRole(owner, venue, heir, VenueRole.Owner);

        Assert.Equal(VenueRole.Owner, _store.RoleOf(heir, venue));
        Assert.Equal(VenueRole.Staff, _store.RoleOf(owner, venue));
    }

    [Fact]
    public void TheOwner_CannotLeave_OrDeleteTheirProfile_WhileOwning()
    {
        var owner = NewProfile("Owner");
        var venue = _store.CreateVenue(owner, "The Gilded Moogle", "");

        Assert.Throws<InvalidOperationException>(() => _store.LeaveVenue(owner, venue));
        Assert.Throws<InvalidOperationException>(() => _store.DeleteProfile(owner));

        _store.DeleteVenue(owner, venue);
        _store.DeleteProfile(owner); // now fine
    }

    [Fact]
    public void VenueOwnership_IsCapped()
    {
        var owner = NewProfile("Owner");
        for (var i = 0; i < CommunityStore.MaxOwnedVenues; i++)
            _store.CreateVenue(owner, $"Venue {i}", "");

        Assert.Throws<InvalidOperationException>(() => _store.CreateVenue(owner, "One Too Many", ""));
    }

    [Fact]
    public void Leaderboard_CountsOnlyGamesWithTwoRealPlayers_ButStatsCountEverything()
    {
        var a = NewProfile("Alice");
        var b = NewProfile("Bob");
        var venue = _store.CreateVenue(a, "The Gilded Moogle", "");
        _store.JoinVenue(b, _store.GetVenue(a, venue, []).JoinCode!);

        _store.RecordResult(venue, "pig", [a, b], winnerProfileId: a);
        _store.RecordResult(venue, "pig", [a, b], winnerProfileId: b);
        _store.RecordResult(venue, "liarsdice", [a, b], winnerProfileId: a);
        _store.RecordResult(venue, "pig", [a], winnerProfileId: a); // solo against bots: unranked

        var overall = _store.Leaderboard(a, venue, null);
        Assert.Equal(["Alice", "Bob"], overall.Select(r => r.DisplayName));
        Assert.Equal((3, 2), (overall[0].Played, overall[0].Won));

        var pigOnly = _store.Leaderboard(a, venue, "pig");
        Assert.Equal((2, 1), (pigOnly[0].Played, pigOnly[0].Won));

        var stats = _store.Stats(a).Single(s => s.GameType == "pig");
        Assert.Equal((3, 2), (stats.Played, stats.Won)); // the solo win still shows on the player's own record
    }

    [Fact]
    public void DeletingAProfile_RemovesItFromLeaderboards_AndKeepsOthersIntact()
    {
        var a = NewProfile("Alice");
        var b = NewProfile("Bob");
        var venue = _store.CreateVenue(a, "The Gilded Moogle", "");
        _store.JoinVenue(b, _store.GetVenue(a, venue, []).JoinCode!);
        _store.RecordResult(venue, "pig", [a, b], winnerProfileId: a);

        _store.DeleteProfile(b);

        var board = _store.Leaderboard(a, venue, null);
        Assert.Equal("Alice", Assert.Single(board).DisplayName);
        Assert.Single(_store.GetVenue(a, venue, []).Members);
    }

    [Fact]
    public void OnlyCurrentMembers_EarnAPlaceOnTheBoard()
    {
        var a = NewProfile("Alice");
        var b = NewProfile("Bob");
        var c = NewProfile("Carol");
        var venue = _store.CreateVenue(a, "The Gilded Moogle", "");
        var code = _store.GetVenue(a, venue, []).JoinCode!;
        _store.JoinVenue(b, code);
        _store.JoinVenue(c, code);

        _store.RecordResult(venue, "pig", [a, b, c], winnerProfileId: c);
        _store.Kick(a, venue, c); // removed after winning: comes off the board

        Assert.Equal(["Alice", "Bob"], _store.Leaderboard(a, venue, null).Select(r => r.DisplayName).Order());

        // And someone removed DURING a game isn't credited for it at all.
        _store.RecordResult(venue, "pig", [a, b, c], winnerProfileId: c);
        _store.JoinVenue(c, code);
        var carol = _store.Leaderboard(a, venue, null).Single(r => r.DisplayName == "Carol");
        Assert.Equal((1, 1), (carol.Played, carol.Won)); // only the game she was a member for
    }

    [Fact]
    public void AResultForADeletedVenueOrProfile_IsStillRecordedSafely()
    {
        var a = NewProfile("Alice");
        var b = NewProfile("Bob");
        var venue = _store.CreateVenue(a, "Short Lived", "");
        _store.JoinVenue(b, _store.GetVenue(a, venue, []).JoinCode!);
        _store.DeleteVenue(a, venue);
        _store.DeleteProfile(b);

        _store.RecordResult(venue, "pig", [a, b], winnerProfileId: a); // the table outlived both

        var stat = Assert.Single(_store.Stats(a));
        Assert.Equal((1, 1), (stat.Played, stat.Won));
    }

    [Fact]
    public void AbandonedProfiles_ArePurged_ButAnyoneWithAFootprintIsKept()
    {
        var drifter = NewProfile("Drifter");
        var member = NewProfile("Member");
        var player = NewProfile("Player");
        _store.CreateVenue(member, "The Gilded Moogle", "");
        _store.RecordResult(null, "pig", [player], winnerProfileId: player);

        Assert.Equal(0, _store.PurgeAbandonedProfiles(DateTime.UtcNow.AddDays(-30))); // all seen just now
        Assert.Equal(1, _store.PurgeAbandonedProfiles(DateTime.UtcNow.AddMinutes(1))); // "not seen since the future"

        Assert.Empty(_store.Stats(drifter));
        Assert.Single(_store.VenuesFor(member));
        Assert.Single(_store.Stats(player));
    }

    [Fact]
    public void ReopeningTheDatabase_KeepsEverything()
    {
        var (profile, token) = _store.Identify(null, "Mina");
        var venue = _store.CreateVenue(profile.Id, "The Gilded Moogle", "");

        var reopened = new CommunityStore(new TavernDb(_path)); // migrations must be a no-op the second time
        Assert.Equal(profile.Id, reopened.Identify(token, "x").Profile.Id);
        Assert.Equal(venue, Assert.Single(reopened.VenuesFor(profile.Id)).Id);
    }
}
