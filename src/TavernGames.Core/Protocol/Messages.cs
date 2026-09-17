using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TavernGames.Core.Platform;

namespace TavernGames.Core.Protocol;

/// <summary>
/// Base type for every message exchanged over the WebSocket. Serialized
/// polymorphically via a "$type" discriminator so both ends share one contract.
/// The set of message types is assembled at startup from the platform messages
/// below plus whatever each game in the <see cref="GameCatalog"/> registers, so
/// adding a game never edits this file.
/// </summary>
public abstract record NetMessage
{
    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static NetMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<NetMessage>(json, Options);

    private static readonly (Type Type, string Name)[] PlatformMessages =
    [
        // Client -> Server
        (typeof(CreateRoom), "createRoom"),
        (typeof(JoinRoom), "joinRoom"),
        (typeof(Spectate), "spectate"),
        (typeof(LeaveRoom), "leaveRoom"),
        (typeof(StartGame), "startGame"),
        (typeof(AddBot), "addBot"),
        (typeof(RemoveBot), "removeBot"),
        // Server -> Client
        (typeof(ErrorMessage), "error"),
        (typeof(Identity), "identity"),
        (typeof(SpectateAccepted), "spectateAccepted"),
        (typeof(RoomUpdate), "roomUpdate"),
        (typeof(GameEnded), "gameEnded"),
        (typeof(RemovedFromRoom), "removedFromRoom"),
        // Profiles and venues, client -> server
        (typeof(Hello), "hello"),
        (typeof(UpdateProfile), "profile.update"),
        (typeof(GetStats), "profile.getStats"),
        (typeof(DeleteProfile), "profile.delete"),
        (typeof(CreateVenue), "venue.create"),
        (typeof(JoinVenue), "venue.join"),
        (typeof(LeaveVenue), "venue.leave"),
        (typeof(GetVenue), "venue.get"),
        (typeof(UpdateVenue), "venue.update"),
        (typeof(DeleteVenue), "venue.delete"),
        (typeof(RegenerateVenueCode), "venue.regenerateCode"),
        (typeof(SetVenueRole), "venue.setRole"),
        (typeof(KickFromVenue), "venue.kick"),
        (typeof(GetLeaderboard), "venue.getLeaderboard"),
        // Profiles and venues, server -> client
        (typeof(Welcome), "welcome"),
        (typeof(ProfileUpdated), "profile.updated"),
        (typeof(Stats), "profile.stats"),
        (typeof(ProfileDeleted), "profile.deleted"),
        (typeof(VenueList), "venue.list"),
        (typeof(VenueDetails), "venue.details"),
        (typeof(VenueLeaderboard), "venue.leaderboard"),
    ];

    // Declared after PlatformMessages on purpose: static initializers run in textual order.
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var polymorphism = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$type",
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
        };
        foreach (var (type, name) in PlatformMessages.Concat(GameCatalog.Games.SelectMany(g => g.Messages)))
            polymorphism.DerivedTypes.Add(new JsonDerivedType(type, name));

        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    info =>
                    {
                        if (info.Type == typeof(NetMessage))
                            info.PolymorphismOptions = polymorphism;
                    },
                },
            },
        };
    }
}

// ---------- Client -> Server ----------

/// <summary>
/// Opens a room for <paramref name="GameType"/> (see <see cref="GameCatalog"/>).
/// <paramref name="Options"/> are the game's own settings; missing or out-of-range
/// values fall back to that game's defaults. <paramref name="TurnDelayMs"/> paces bots.
/// With a <paramref name="VenueId"/> the table is hosted by that venue (staff and owner
/// only): members can find and join it, and its result counts on the venue's leaderboard.
/// </summary>
public sealed record CreateRoom(
    string PlayerName,
    string GameType,
    Dictionary<string, int>? Options = null,
    int TurnDelayMs = 1500,
    string? VenueId = null) : NetMessage;

public sealed record JoinRoom(string RoomCode, string PlayerName) : NetMessage;

/// <summary>Join a room as a watcher: receive public state, but no hand and no actions.</summary>
public sealed record Spectate(string RoomCode) : NetMessage;

public sealed record LeaveRoom : NetMessage;

/// <summary>Host-only.</summary>
public sealed record StartGame : NetMessage;

/// <summary>Host-only, lobby-only: add a computer-controlled player.</summary>
public sealed record AddBot : NetMessage;

/// <summary>Host-only, lobby-only: remove a previously added bot.</summary>
public sealed record RemoveBot(string PlayerId) : NetMessage;

// ---------- Server -> Client ----------

public sealed record ErrorMessage(string Text) : NetMessage;

/// <summary>Sent once after create/join so the client learns its own id and room code.</summary>
public sealed record Identity(string PlayerId, string RoomCode, string HostId) : NetMessage;

/// <summary>Confirms a spectator is now watching the given room.</summary>
public sealed record SpectateAccepted(string RoomCode) : NetMessage;

/// <summary>Broadcast whenever the roster or phase changes (join/leave/start).</summary>
public sealed record RoomUpdate(
    string RoomCode,
    string HostId,
    string GameType,
    GamePhase Phase,
    PlayerPublic[] Players,
    string? VenueName = null) : NetMessage;

/// <summary>The game is over. Shared by every game.</summary>
public sealed record GameEnded(string WinnerId, PlayerPublic[] Players) : NetMessage;

/// <summary>
/// The server took this connection out of its room (for example, it was removed from the
/// venue hosting the table). The client should return to the lobby and show the reason.
/// </summary>
public sealed record RemovedFromRoom(string Reason) : NetMessage;
