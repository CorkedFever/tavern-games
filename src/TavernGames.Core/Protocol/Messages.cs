using System.Text.Json;
using System.Text.Json.Serialization;

namespace TavernGames.Core.Protocol;

/// <summary>
/// Base type for every message exchanged over the WebSocket. Serialized
/// polymorphically via a "$type" discriminator so both ends share one contract.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
// Client -> Server
[JsonDerivedType(typeof(CreateRoom), "createRoom")]
[JsonDerivedType(typeof(JoinRoom), "joinRoom")]
[JsonDerivedType(typeof(StartGame), "startGame")]
[JsonDerivedType(typeof(PlaceBid), "placeBid")]
[JsonDerivedType(typeof(Challenge), "challenge")]
[JsonDerivedType(typeof(LeaveRoom), "leaveRoom")]
[JsonDerivedType(typeof(AddBot), "addBot")]
[JsonDerivedType(typeof(RemoveBot), "removeBot")]
[JsonDerivedType(typeof(Spectate), "spectate")]
// Server -> Client
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(Identity), "identity")]
[JsonDerivedType(typeof(RoomUpdate), "roomUpdate")]
[JsonDerivedType(typeof(RoundStarted), "roundStarted")]
[JsonDerivedType(typeof(YourHand), "yourHand")]
[JsonDerivedType(typeof(BidPlaced), "bidPlaced")]
[JsonDerivedType(typeof(ChallengeResolved), "challengeResolved")]
[JsonDerivedType(typeof(GameEnded), "gameEnded")]
[JsonDerivedType(typeof(SpectateAccepted), "spectateAccepted")]
public abstract record NetMessage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static NetMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<NetMessage>(json, Options);
}

// ---------- Client -> Server ----------

public sealed record CreateRoom(string PlayerName, int StartingDice, int TurnDelayMs = 1500) : NetMessage;
public sealed record JoinRoom(string RoomCode, string PlayerName) : NetMessage;
public sealed record StartGame : NetMessage;
public sealed record PlaceBid(int Quantity, int FaceValue) : NetMessage;
public sealed record Challenge : NetMessage;
public sealed record LeaveRoom : NetMessage;

/// <summary>Host-only, lobby-only: add a computer-controlled player.</summary>
public sealed record AddBot : NetMessage;

/// <summary>Host-only, lobby-only: remove a previously added bot.</summary>
public sealed record RemoveBot(string PlayerId) : NetMessage;

/// <summary>Join a room as a watcher: receive public state, but no hand and no actions.</summary>
public sealed record Spectate(string RoomCode) : NetMessage;

// ---------- Server -> Client ----------

public sealed record ErrorMessage(string Text) : NetMessage;

/// <summary>Sent once after create/join so the client learns its own id and room code.</summary>
public sealed record Identity(string PlayerId, string RoomCode, string HostId) : NetMessage;

/// <summary>Broadcast whenever the roster or phase changes (join/leave/start/end).</summary>
public sealed record RoomUpdate(
    string RoomCode,
    string HostId,
    GamePhase Phase,
    PlayerPublic[] Players) : NetMessage;

/// <summary>Broadcast at the start of every round. Each client also receives its own <see cref="YourHand"/>.</summary>
public sealed record RoundStarted(
    string CurrentPlayerId,
    PlayerPublic[] Players) : NetMessage;

/// <summary>Private: this connection's secret dice for the current round.</summary>
public sealed record YourHand(int[] Dice) : NetMessage;

public sealed record BidPlaced(
    string PlayerId,
    BidDto Bid,
    string NextPlayerId) : NetMessage;

public sealed record ChallengeResolved(
    BidDto Bid,
    string ChallengerId,
    string BidderId,
    int ActualCount,
    bool BidWasValid,
    string LoserId,
    bool LoserEliminated,
    HandReveal[] Reveal,
    bool GameOver,
    string? WinnerId,
    string? NextStarterId) : NetMessage;

public sealed record GameEnded(string WinnerId, PlayerPublic[] Players) : NetMessage;

/// <summary>Confirms a spectator is now watching the given room.</summary>
public sealed record SpectateAccepted(string RoomCode) : NetMessage;
