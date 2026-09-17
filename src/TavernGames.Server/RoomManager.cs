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

    public GameRoom Create(string gameType, IReadOnlyDictionary<string, int>? options, int turnDelayMs)
    {
        if (_rooms.Count >= _maxRooms)
            throw new InvalidOperationException("The server is at capacity — please try again later.");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = NewCode();
            var room = new GameRoom(code, GameCatalog.Create(gameType, options), turnDelayMs);
            if (_rooms.TryAdd(code, room))
                return room;
        }
        throw new InvalidOperationException("Could not allocate a unique room code.");
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
