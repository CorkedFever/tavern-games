namespace TavernGames.Core.Protocol;

// Profiles and venues: the persistent, social layer that sits beside the game rooms.

public sealed record ProfilePublic(string Id, string DisplayName, string Tagline);

public sealed record GameStat(string GameType, int Played, int Won);

/// <summary>Ordered so that a higher value can do everything a lower one can.</summary>
public enum VenueRole
{
    Member = 0,
    Staff = 1,
    Owner = 2,
}

public sealed record VenueSummary(string Id, string Name, VenueRole MyRole, int MemberCount);

public sealed record VenueMember(string ProfileId, string DisplayName, string Tagline, VenueRole Role);

/// <summary>A room currently open under a venue.</summary>
public sealed record VenueTable(string RoomCode, string GameType, string HostName, int Players, int MaxPlayers, GamePhase Phase);

public sealed record LeaderboardRow(string ProfileId, string DisplayName, int Played, int Won);

/// <summary><paramref name="JoinCode"/> is only filled in for staff and the owner.</summary>
public sealed record VenueInfo(
    string Id,
    string Name,
    string Description,
    string? JoinCode,
    VenueRole MyRole,
    VenueMember[] Members,
    VenueTable[] Tables);

// ---------- Client -> Server ----------

/// <summary>
/// Identifies this plugin install. Send the token the server issued last time to get
/// the same profile back; send none (or a stale one) and a fresh profile is created
/// using <paramref name="DisplayName"/>. There are no passwords: the token is the key.
/// </summary>
public sealed record Hello(string? Token, string DisplayName) : NetMessage;

public sealed record UpdateProfile(string DisplayName, string Tagline) : NetMessage;
public sealed record GetStats : NetMessage;

/// <summary>Erases the profile, its memberships and its results. Refused while it still owns a venue.</summary>
public sealed record DeleteProfile : NetMessage;

public sealed record CreateVenue(string Name, string Description) : NetMessage;
public sealed record JoinVenue(string Code) : NetMessage;
public sealed record LeaveVenue(string VenueId) : NetMessage;
public sealed record GetVenue(string VenueId) : NetMessage;

/// <summary>Owner-only.</summary>
public sealed record UpdateVenue(string VenueId, string Name, string Description) : NetMessage;

/// <summary>Owner-only. Erases the venue, its memberships and detaches its results.</summary>
public sealed record DeleteVenue(string VenueId) : NetMessage;

/// <summary>Staff and owner. The old code stops working at once.</summary>
public sealed record RegenerateVenueCode(string VenueId) : NetMessage;

/// <summary>Owner-only. Setting someone to <see cref="VenueRole.Owner"/> hands the venue over; the caller becomes staff.</summary>
public sealed record SetVenueRole(string VenueId, string ProfileId, VenueRole Role) : NetMessage;

/// <summary>Staff may remove members; the owner may remove anyone but themselves.</summary>
public sealed record KickFromVenue(string VenueId, string ProfileId) : NetMessage;

/// <summary><paramref name="GameType"/> null means all games combined.</summary>
public sealed record GetLeaderboard(string VenueId, string? GameType) : NetMessage;

// ---------- Server -> Client ----------

public sealed record Welcome(ProfilePublic Profile, string Token, VenueSummary[] Venues) : NetMessage;
public sealed record ProfileUpdated(ProfilePublic Profile) : NetMessage;
public sealed record Stats(GameStat[] Games) : NetMessage;
public sealed record ProfileDeleted : NetMessage;

/// <summary>Sent whenever the caller's set of venues (or their role in one) changes.</summary>
public sealed record VenueList(VenueSummary[] Venues) : NetMessage;

public sealed record VenueDetails(VenueInfo Venue) : NetMessage;
public sealed record VenueLeaderboard(string VenueId, string? GameType, LeaderboardRow[] Rows) : NetMessage;
