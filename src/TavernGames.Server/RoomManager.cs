using System.Collections.Concurrent;
using System.Security.Cryptography;
using TavernGames.Core.Platform;

namespace TavernGames.Server;

/// <summary>Owns the set of live rooms and matchmaking by short room code.</summary>
public sealed class RoomManager
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no easily-confused chars
    private readonly ConcurrentDictionary<string, GameRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);

    // Caps concurrent rooms on a public instance (override with TAVERN_MAX_ROOMS).
    private readonly int _maxRooms =
        int.TryParse(Environment.GetEnvironmentVariable("TAVERN_MAX_ROOMS"), out var m) && m > 0 ? m : 200;

    public int RoomCount => _rooms.Count;

    public GameRoom Create(
        string gameType,
        IReadOnlyDictionary<string, int>? options,
        int turnDelayMs,
        string? venueId = null,
        string? venueName = null,
        Action<GameResult>? onResult = null)
    {
        if (_rooms.Count >= _maxRooms)
            throw new InvalidOperationException("The server is at capacity. Please try again later.");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = NewCode();
            var room = new GameRoom(code, GameCatalog.Create(gameType, options), turnDelayMs, venueId, venueName, onResult);
            if (_rooms.TryAdd(code, room))
                return room;
        }
        throw new InvalidOperationException("Could not allocate a unique room code.");
    }

    /// <summary>Tables currently open under a venue that are still worth listing (not finished).</summary>
    public TavernGames.Core.Protocol.VenueTable[] TablesFor(string venueId) =>
        _rooms.Values
            .Where(r => r.VenueId == venueId)
            .Select(r => r.Table)
            .Where(t => t.Phase != TavernGames.Core.GamePhase.GameOver)
            .OrderBy(t => t.Phase)
            .ToArray();

    /// <summary>Removes a profile from every table a venue is hosting (they left, or were removed from, the venue).</summary>
    public async Task EvictFromVenueTablesAsync(string venueId, string profileId, string reason)
    {
        foreach (var room in _rooms.Values.Where(r => r.VenueId == venueId).ToList())
        {
            await room.EvictProfileAsync(profileId, reason);
            if (room.IsEmpty) Remove(room.Code);
        }
    }

    public bool TryGet(string code, out GameRoom room) =>
        _rooms.TryGetValue(code, out room!);

    public void Remove(string code) => _rooms.TryRemove(code, out _);

    private static string NewCode()
    {
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }
}
