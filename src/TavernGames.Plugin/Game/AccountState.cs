using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Game;

/// <summary>
/// The player's profile and venues as last reported by the server. Like
/// <see cref="GameSession"/> it is only touched on the framework thread. It outlives
/// rooms (you keep your profile between tables) but not the connection.
/// </summary>
public sealed class AccountState
{
    public ProfilePublic? Profile { get; private set; }
    public List<VenueSummary> Venues { get; } = new();
    public GameStat[] Stats { get; private set; } = [];

    /// <summary>The venue currently open in the Venues tab.</summary>
    public VenueInfo? Venue { get; private set; }

    public LeaderboardRow[] Leaderboard { get; private set; } = [];
    public string LeaderboardVenueId { get; private set; } = "";

    /// <summary>Set when the server issues a token; the plugin saves it and clears this.</summary>
    public string? IssuedToken { get; set; }

    /// <summary>True once after the profile was deleted, so the plugin can forget the saved token.</summary>
    public bool ProfileWasDeleted { get; set; }

    public bool HasProfile => Profile is not null;

    /// <summary>Venues where I may host a table.</summary>
    public IEnumerable<VenueSummary> HostableVenues => Venues.Where(v => v.MyRole >= VenueRole.Staff);

    public void Reset()
    {
        Profile = null;
        Venues.Clear();
        Stats = [];
        Venue = null;
        Leaderboard = [];
        LeaderboardVenueId = "";
        IssuedToken = null;
    }

    public void CloseVenue()
    {
        Venue = null;
        Leaderboard = [];
        LeaderboardVenueId = "";
    }

    /// <summary>Returns false if the message isn't about profiles or venues.</summary>
    public bool Apply(NetMessage message)
    {
        switch (message)
        {
            case Welcome welcome:
                Profile = welcome.Profile;
                IssuedToken = welcome.Token;
                SetVenues(welcome.Venues);
                return true;

            case ProfileUpdated updated:
                Profile = updated.Profile;
                return true;

            case Stats stats:
                Stats = stats.Games;
                return true;

            case ProfileDeleted:
                Reset();
                ProfileWasDeleted = true;
                return true;

            case VenueList list:
                SetVenues(list.Venues);
                return true;

            case VenueDetails details:
                Venue = details.Venue;
                return true;

            case VenueLeaderboard board:
                Leaderboard = board.Rows;
                LeaderboardVenueId = board.VenueId;
                return true;

            default:
                return false;
        }
    }

    private void SetVenues(VenueSummary[] venues)
    {
        Venues.Clear();
        Venues.AddRange(venues);
        if (Venue is not null && Venues.All(v => v.Id != Venue.Id))
            CloseVenue(); // left, kicked, or the venue was deleted
    }
}
