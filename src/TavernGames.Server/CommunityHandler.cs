using TavernGames.Core.Protocol;
using TavernGames.Server.Data;

namespace TavernGames.Server;

/// <summary>
/// Handles the profile and venue messages for a connection. Stateless: who the caller
/// is lives on the <see cref="ClientConnection"/>, and what they may do is decided by
/// the <see cref="CommunityStore"/>.
/// </summary>
public sealed class CommunityHandler(CommunityStore store, RoomManager rooms)
{
    /// <summary>Returns false if the message isn't a profile or venue message.</summary>
    public async Task<bool> TryHandleAsync(ClientConnection conn, NetMessage message)
    {
        if (message is not (Hello or UpdateProfile or GetStats or DeleteProfile
            or CreateVenue or JoinVenue or LeaveVenue or GetVenue or UpdateVenue or DeleteVenue
            or RegenerateVenueCode or SetVenueRole or KickFromVenue or GetLeaderboard))
            return false;

        try
        {
            await HandleAsync(conn, message);
        }
        catch (InvalidOperationException ex)
        {
            await conn.SendAsync(new ErrorMessage(ex.Message));
        }
        return true;
    }

    private async Task HandleAsync(ClientConnection conn, NetMessage message)
    {
        if (message is Hello hello)
        {
            if (conn.ProfileId is not null)
                throw new InvalidOperationException("This connection is already identified.");

            var (profile, token) = store.Identify(hello.Token, hello.DisplayName);
            conn.ProfileId = profile.Id;
            conn.DisplayName = profile.DisplayName;
            await conn.SendAsync(new Welcome(profile, token, store.VenuesFor(profile.Id)));
            return;
        }

        var me = conn.ProfileId
            ?? throw new InvalidOperationException("Say hello first: this needs a player profile.");

        switch (message)
        {
            case UpdateProfile update:
                var updated = store.UpdateProfile(me, update.DisplayName, update.Tagline);
                conn.DisplayName = updated.DisplayName;
                await conn.SendAsync(new ProfileUpdated(updated));
                break;

            case GetStats:
                await conn.SendAsync(new Stats(store.Stats(me)));
                break;

            case DeleteProfile:
                store.DeleteProfile(me);
                conn.ProfileId = null;
                conn.DisplayName = null;
                await conn.SendAsync(new ProfileDeleted());
                break;

            case CreateVenue create:
                await SendVenueAsync(conn, me, store.CreateVenue(me, create.Name, create.Description), listChanged: true);
                break;

            case JoinVenue join:
                await SendVenueAsync(conn, me, store.JoinVenue(me, join.Code), listChanged: true);
                break;

            case LeaveVenue leave:
                store.LeaveVenue(me, leave.VenueId);
                await conn.SendAsync(new VenueList(store.VenuesFor(me)));
                break;

            case GetVenue get:
                await SendVenueAsync(conn, me, get.VenueId, listChanged: false);
                break;

            case UpdateVenue update:
                store.UpdateVenue(me, update.VenueId, update.Name, update.Description);
                await SendVenueAsync(conn, me, update.VenueId, listChanged: true);
                break;

            case DeleteVenue delete:
                store.DeleteVenue(me, delete.VenueId);
                await conn.SendAsync(new VenueList(store.VenuesFor(me)));
                break;

            case RegenerateVenueCode regenerate:
                store.RegenerateCode(me, regenerate.VenueId);
                await SendVenueAsync(conn, me, regenerate.VenueId, listChanged: false);
                break;

            case SetVenueRole setRole:
                store.SetRole(me, setRole.VenueId, setRole.ProfileId, setRole.Role);
                await SendVenueAsync(conn, me, setRole.VenueId, listChanged: true); // a handover changes my own role
                break;

            case KickFromVenue kick:
                store.Kick(me, kick.VenueId, kick.ProfileId);
                await SendVenueAsync(conn, me, kick.VenueId, listChanged: true);
                break;

            case GetLeaderboard board:
                await conn.SendAsync(new VenueLeaderboard(
                    board.VenueId, board.GameType, store.Leaderboard(me, board.VenueId, board.GameType)));
                break;
        }
    }

    private async Task SendVenueAsync(ClientConnection conn, string profileId, string venueId, bool listChanged)
    {
        if (listChanged)
            await conn.SendAsync(new VenueList(store.VenuesFor(profileId)));
        await conn.SendAsync(new VenueDetails(store.GetVenue(profileId, venueId, rooms.TablesFor(venueId))));
    }
}
