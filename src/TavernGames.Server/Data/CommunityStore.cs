using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TavernGames.Core.Protocol;

namespace TavernGames.Server.Data;

/// <summary>
/// Profiles, venues, memberships and game results. Every rule about who may do what
/// lives here (not in the message handlers), so a rule can't be bypassed by a new
/// message path. Rule violations throw <see cref="InvalidOperationException"/> with a
/// message that is safe to show the player.
/// </summary>
public sealed class CommunityStore(TavernDb db)
{
    public const int MaxOwnedVenues = 5;
    public const int MaxMemberships = 50;
    public const int MaxVenueMembers = 500;
    public const int LeaderboardSize = 50;

    /// <summary>A result only counts on leaderboards when at least this many real players sat down.</summary>
    public const int RankedHumanCount = 2;

    private const string IdAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no easily-confused chars

    // ---------------------------------------------------------------- profiles

    /// <summary>
    /// Returns the profile the token belongs to, or creates a new one. The raw token is
    /// only ever held by the client; we keep its SHA-256.
    /// </summary>
    public (ProfilePublic Profile, string Token) Identify(string? token, string displayName)
    {
        using var connection = db.Open();
        var now = Now();

        if (!string.IsNullOrWhiteSpace(token))
        {
            var existing = connection.Query(
                "SELECT id, display_name, tagline FROM profiles WHERE token_hash = $hash",
                ReadProfile, ("$hash", Hash(token))).FirstOrDefault();
            if (existing is not null)
            {
                connection.Execute("UPDATE profiles SET last_seen_at = $now WHERE id = $id", ("$now", now), ("$id", existing.Id));
                return (existing, token);
            }
        }

        var profile = new ProfilePublic(NewId(8), CleanName(displayName), "");
        var newToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        connection.Execute(
            """
            INSERT INTO profiles (id, token_hash, display_name, tagline, created_at, last_seen_at)
            VALUES ($id, $hash, $name, '', $now, $now)
            """,
            ("$id", profile.Id), ("$hash", Hash(newToken)), ("$name", profile.DisplayName), ("$now", now));
        return (profile, newToken);
    }

    public ProfilePublic UpdateProfile(string profileId, string displayName, string tagline)
    {
        var profile = new ProfilePublic(profileId, CleanName(displayName), Clean(tagline, 60));
        using var connection = db.Open();
        connection.Execute(
            "UPDATE profiles SET display_name = $name, tagline = $tagline WHERE id = $id",
            ("$name", profile.DisplayName), ("$tagline", profile.Tagline), ("$id", profileId));
        return profile;
    }

    /// <summary>Lifetime results per game, including games against bots.</summary>
    public GameStat[] Stats(string profileId)
    {
        using var connection = db.Open();
        return connection.Query(
            """
            SELECT r.game_type, COUNT(*), SUM(p.won)
            FROM game_result_players p JOIN game_results r ON r.id = p.result_id
            WHERE p.profile_id = $id
            GROUP BY r.game_type ORDER BY r.game_type
            """,
            row => new GameStat(row.GetString(0), row.GetInt32(1), row.GetInt32(2)),
            ("$id", profileId)).ToArray();
    }

    public void DeleteProfile(string profileId)
    {
        using var connection = db.Open();
        var owned = connection.Scalar<long>(
            "SELECT COUNT(*) FROM venue_members WHERE profile_id = $id AND role = $owner",
            ("$id", profileId), ("$owner", (int)VenueRole.Owner));
        if (owned > 0)
            throw new InvalidOperationException("You still own a venue. Hand it over or delete it first.");

        // Memberships and result rows go with it (ON DELETE CASCADE).
        connection.Execute("DELETE FROM profiles WHERE id = $id", ("$id", profileId));
    }

    // ------------------------------------------------------------------ venues

    public VenueSummary[] VenuesFor(string profileId)
    {
        using var connection = db.Open();
        return connection.Query(
            """
            SELECT v.id, v.name, m.role,
                   (SELECT COUNT(*) FROM venue_members c WHERE c.venue_id = v.id)
            FROM venue_members m JOIN venues v ON v.id = m.venue_id
            WHERE m.profile_id = $id
            ORDER BY v.name COLLATE NOCASE
            """,
            row => new VenueSummary(row.GetString(0), row.GetString(1), (VenueRole)row.GetInt32(2), row.GetInt32(3)),
            ("$id", profileId)).ToArray();
    }

    public string CreateVenue(string profileId, string name, string description)
    {
        name = CleanVenueName(name);
        using var connection = db.Open();

        var owned = connection.Scalar<long>(
            "SELECT COUNT(*) FROM venue_members WHERE profile_id = $id AND role = $owner",
            ("$id", profileId), ("$owner", (int)VenueRole.Owner));
        if (owned >= MaxOwnedVenues)
            throw new InvalidOperationException($"You can own at most {MaxOwnedVenues} venues.");
        EnsureRoomForAnotherMembership(connection, profileId);

        var venueId = NewId(8);
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            "INSERT INTO venues (id, name, description, join_code, created_at) VALUES ($id, $name, $desc, $code, $now)",
            ("$id", venueId), ("$name", name), ("$desc", Clean(description, 200)), ("$code", NewJoinCode(connection)), ("$now", Now()));
        connection.Execute(
            "INSERT INTO venue_members (venue_id, profile_id, role, joined_at) VALUES ($venue, $profile, $role, $now)",
            ("$venue", venueId), ("$profile", profileId), ("$role", (int)VenueRole.Owner), ("$now", Now()));
        transaction.Commit();
        return venueId;
    }

    /// <summary>Joins by code. Joining a venue you're already in is not an error; it just returns it.</summary>
    public string JoinVenue(string profileId, string code)
    {
        using var connection = db.Open();
        var venueId = connection.Scalar<string>(
            "SELECT id FROM venues WHERE join_code = $code COLLATE NOCASE", ("$code", (code ?? "").Trim()));
        if (venueId is null)
            throw new InvalidOperationException("No venue uses that code. Codes change when a venue regenerates theirs.");

        if (RoleOf(connection, profileId, venueId) is not null)
            return venueId;

        EnsureRoomForAnotherMembership(connection, profileId);
        var size = connection.Scalar<long>("SELECT COUNT(*) FROM venue_members WHERE venue_id = $v", ("$v", venueId));
        if (size >= MaxVenueMembers)
            throw new InvalidOperationException("That venue is full.");

        connection.Execute(
            "INSERT INTO venue_members (venue_id, profile_id, role, joined_at) VALUES ($venue, $profile, $role, $now)",
            ("$venue", venueId), ("$profile", profileId), ("$role", (int)VenueRole.Member), ("$now", Now()));
        return venueId;
    }

    public void LeaveVenue(string profileId, string venueId)
    {
        using var connection = db.Open();
        var role = RequireMember(connection, profileId, venueId);
        if (role == VenueRole.Owner)
            throw new InvalidOperationException("The owner can't leave. Hand the venue over or delete it.");
        RemoveMember(connection, venueId, profileId);
    }

    public VenueRole? RoleOf(string profileId, string venueId)
    {
        using var connection = db.Open();
        return RoleOf(connection, profileId, venueId);
    }

    public string? VenueName(string venueId)
    {
        using var connection = db.Open();
        return connection.Scalar<string>("SELECT name FROM venues WHERE id = $id", ("$id", venueId));
    }

    /// <summary>Members only. The join code is withheld from plain members.</summary>
    public VenueInfo GetVenue(string profileId, string venueId, VenueTable[] tables)
    {
        using var connection = db.Open();
        var myRole = RequireMember(connection, profileId, venueId);

        var (name, description, code) = connection.Query(
            "SELECT name, description, join_code FROM venues WHERE id = $id",
            row => (row.GetString(0), row.GetString(1), row.GetString(2)), ("$id", venueId)).Single();

        var members = connection.Query(
            """
            SELECT p.id, p.display_name, p.tagline, m.role
            FROM venue_members m JOIN profiles p ON p.id = m.profile_id
            WHERE m.venue_id = $id
            ORDER BY m.role DESC, p.display_name COLLATE NOCASE
            """,
            row => new VenueMember(row.GetString(0), row.GetString(1), row.GetString(2), (VenueRole)row.GetInt32(3)),
            ("$id", venueId)).ToArray();

        return new VenueInfo(venueId, name, description, myRole >= VenueRole.Staff ? code : null, myRole, members, tables);
    }

    public void UpdateVenue(string profileId, string venueId, string name, string description)
    {
        using var connection = db.Open();
        Require(connection, profileId, venueId, VenueRole.Owner, "change the venue's details");
        connection.Execute(
            "UPDATE venues SET name = $name, description = $desc WHERE id = $id",
            ("$name", CleanVenueName(name)), ("$desc", Clean(description, 200)), ("$id", venueId));
    }

    public void DeleteVenue(string profileId, string venueId)
    {
        using var connection = db.Open();
        Require(connection, profileId, venueId, VenueRole.Owner, "delete the venue");
        connection.Execute("DELETE FROM venues WHERE id = $id", ("$id", venueId));
    }

    public void RegenerateCode(string profileId, string venueId)
    {
        using var connection = db.Open();
        Require(connection, profileId, venueId, VenueRole.Staff, "change the join code");
        connection.Execute(
            "UPDATE venues SET join_code = $code WHERE id = $id", ("$code", NewJoinCode(connection)), ("$id", venueId));
    }

    public void SetRole(string profileId, string venueId, string targetId, VenueRole role)
    {
        if (!Enum.IsDefined(role))
            throw new InvalidOperationException("Unknown role.");

        using var connection = db.Open();
        Require(connection, profileId, venueId, VenueRole.Owner, "change roles");
        if (targetId == profileId)
            throw new InvalidOperationException("You can't change your own role. Hand the venue to someone else instead.");
        if (RoleOf(connection, targetId, venueId) is null)
            throw new InvalidOperationException("That player isn't a member of this venue.");

        using var transaction = connection.BeginTransaction();
        SetRole(connection, venueId, targetId, role);
        if (role == VenueRole.Owner)
            SetRole(connection, venueId, profileId, VenueRole.Staff); // a venue has exactly one owner
        transaction.Commit();
    }

    public void Kick(string profileId, string venueId, string targetId)
    {
        using var connection = db.Open();
        var myRole = Require(connection, profileId, venueId, VenueRole.Staff, "remove members");
        var targetRole = RoleOf(connection, targetId, venueId)
            ?? throw new InvalidOperationException("That player isn't a member of this venue.");
        if (targetId == profileId || targetRole >= myRole)
            throw new InvalidOperationException("You can only remove members ranked below you.");
        RemoveMember(connection, venueId, targetId);
    }

    // ----------------------------------------------------------------- results

    /// <summary>Records a finished game for every profiled player who sat down at the start.</summary>
    public void RecordResult(string? venueId, string gameType, IReadOnlyCollection<string> profileIds, string? winnerProfileId)
    {
        if (profileIds.Count == 0) return;

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        var resultId = connection.Scalar<long>(
            """
            INSERT INTO game_results (venue_id, game_type, human_count, ended_at)
            VALUES ($venue, $game, $humans, $now)
            RETURNING id
            """,
            ("$venue", venueId), ("$game", gameType), ("$humans", profileIds.Count), ("$now", Now()));

        foreach (var id in profileIds)
            connection.Execute(
                "INSERT OR IGNORE INTO game_result_players (result_id, profile_id, won) VALUES ($result, $profile, $won)",
                ("$result", resultId), ("$profile", id), ("$won", id == winnerProfileId ? 1 : 0));
        transaction.Commit();
    }

    /// <summary>Members only. Ranked games only. Most wins first; ties go to whoever needed fewer games.</summary>
    public LeaderboardRow[] Leaderboard(string profileId, string venueId, string? gameType)
    {
        using var connection = db.Open();
        RequireMember(connection, profileId, venueId);

        return connection.Query(
            """
            SELECT pr.id, pr.display_name, COUNT(*) AS played, SUM(p.won) AS won
            FROM game_results r
            JOIN game_result_players p ON p.result_id = r.id
            JOIN profiles pr ON pr.id = p.profile_id
            WHERE r.venue_id = $venue AND r.human_count >= $ranked
              AND ($game IS NULL OR r.game_type = $game)
            GROUP BY pr.id, pr.display_name
            ORDER BY won DESC, played ASC, pr.display_name COLLATE NOCASE
            LIMIT $limit
            """,
            row => new LeaderboardRow(row.GetString(0), row.GetString(1), row.GetInt32(2), row.GetInt32(3)),
            ("$venue", venueId), ("$ranked", RankedHumanCount), ("$game", gameType), ("$limit", LeaderboardSize)).ToArray();
    }

    // ----------------------------------------------------------------- helpers

    private static VenueRole? RoleOf(SqliteConnection connection, string profileId, string venueId)
    {
        var role = connection.Scalar<long?>(
            "SELECT role FROM venue_members WHERE venue_id = $venue AND profile_id = $profile",
            ("$venue", venueId), ("$profile", profileId));
        return role is null ? null : (VenueRole)role.Value;
    }

    private static VenueRole RequireMember(SqliteConnection connection, string profileId, string venueId) =>
        RoleOf(connection, profileId, venueId)
        ?? throw new InvalidOperationException("You're not a member of that venue.");

    private static VenueRole Require(SqliteConnection connection, string profileId, string venueId, VenueRole minimum, string action)
    {
        var role = RequireMember(connection, profileId, venueId);
        if (role < minimum)
            throw new InvalidOperationException($"Only the venue's {(minimum == VenueRole.Owner ? "owner" : "staff")} can {action}.");
        return role;
    }

    private static void SetRole(SqliteConnection connection, string venueId, string profileId, VenueRole role) =>
        connection.Execute(
            "UPDATE venue_members SET role = $role WHERE venue_id = $venue AND profile_id = $profile",
            ("$role", (int)role), ("$venue", venueId), ("$profile", profileId));

    private static void RemoveMember(SqliteConnection connection, string venueId, string profileId) =>
        connection.Execute(
            "DELETE FROM venue_members WHERE venue_id = $venue AND profile_id = $profile",
            ("$venue", venueId), ("$profile", profileId));

    private static void EnsureRoomForAnotherMembership(SqliteConnection connection, string profileId)
    {
        var count = connection.Scalar<long>("SELECT COUNT(*) FROM venue_members WHERE profile_id = $id", ("$id", profileId));
        if (count >= MaxMemberships)
            throw new InvalidOperationException($"You can belong to at most {MaxMemberships} venues.");
    }

    private static string NewJoinCode(SqliteConnection connection)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = NewId(6);
            if (connection.Scalar<long>("SELECT COUNT(*) FROM venues WHERE join_code = $code", ("$code", code)) == 0)
                return code;
        }
        throw new InvalidOperationException("Could not allocate a join code. Try again.");
    }

    private static string NewId(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
        return new string(chars);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Now() => DateTime.UtcNow.ToString("O");

    private static ProfilePublic ReadProfile(SqliteDataReader row) =>
        new(row.GetString(0), row.GetString(1), row.GetString(2));

    public static string CleanName(string? name)
    {
        var cleaned = Clean(name, 24);
        return cleaned.Length == 0 ? "Adventurer" : cleaned;
    }

    private static string CleanVenueName(string? name)
    {
        var cleaned = Clean(name, 40);
        if (cleaned.Length < 3)
            throw new InvalidOperationException("A venue name needs at least 3 characters.");
        return cleaned;
    }

    /// <summary>Trims, drops control characters (names end up in chat logs and UIs), and caps the length.</summary>
    private static string Clean(string? text, int maxLength)
    {
        var cleaned = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > maxLength ? cleaned[..maxLength].TrimEnd() : cleaned;
    }
}
