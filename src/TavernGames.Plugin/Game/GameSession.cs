using TavernGames.Core;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Game;

/// <summary>
/// The client's view of the room. Mutated only on the main (framework) thread as
/// inbound messages are drained, then read by the UI in the same frame loop, so it
/// needs no locking. Game-specific state lives in the <see cref="IClientGame"/>s.
/// </summary>
public sealed class GameSession(IReadOnlyList<IClientGame> games)
{
    public const int MaxLog = 100;

    public IReadOnlyList<IClientGame> Games { get; } = games;

    public string MyId { get; private set; } = "";
    public string RoomCode { get; private set; } = "";
    public string HostId { get; private set; } = "";
    public string GameType { get; private set; } = "";
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public bool IsSpectator { get; private set; }
    public string? WinnerId { get; private set; }

    public List<PlayerPublic> Players { get; } = new();
    public List<string> Log { get; } = new();

    /// <summary>The game this room is playing, once the server has told us.</summary>
    public IClientGame? ActiveGame => Games.FirstOrDefault(g => g.GameType == GameType);

    public bool InRoom => RoomCode.Length > 0;
    public bool IsHost => !IsSpectator && MyId.Length > 0 && MyId == HostId;

    /// <summary>I'm a seated player who hasn't been knocked out.</summary>
    public bool AmActivePlayer =>
        !IsSpectator && MyId.Length > 0 && Players.Any(p => p.Id == MyId && !p.Eliminated);

    public string NameOf(string id) =>
        Players.FirstOrDefault(p => p.Id == id)?.Name ?? id;

    public void Reset()
    {
        MyId = RoomCode = HostId = GameType = "";
        Phase = GamePhase.Lobby;
        IsSpectator = false;
        WinnerId = null;
        Players.Clear();
        Log.Clear();
        foreach (var game in Games) game.Reset();
    }

    public void AddLog(string line)
    {
        Log.Add(line);
        if (Log.Count > MaxLog) Log.RemoveRange(0, Log.Count - MaxLog);
    }

    public void SetPlayers(PlayerPublic[] players)
    {
        Players.Clear();
        Players.AddRange(players);
    }

    /// <summary>Applies a server message: room-level messages here, anything else by the game it belongs to.</summary>
    public void Apply(NetMessage message)
    {
        switch (message)
        {
            case Identity id:
                MyId = id.PlayerId;
                RoomCode = id.RoomCode;
                HostId = id.HostId;
                AddLog($"Joined room {id.RoomCode}.");
                break;

            case SpectateAccepted spec:
                IsSpectator = true;
                RoomCode = spec.RoomCode;
                AddLog($"Now spectating room {spec.RoomCode}.");
                break;

            case RoomUpdate update:
                RoomCode = update.RoomCode;
                HostId = update.HostId;
                GameType = update.GameType;
                Phase = update.Phase;
                SetPlayers(update.Players);
                break;

            case GameEnded ended:
                Phase = GamePhase.GameOver;
                WinnerId = ended.WinnerId;
                SetPlayers(ended.Players);
                AddLog($"Game over. {NameOf(ended.WinnerId)} wins!");
                break;

            case ErrorMessage error:
                AddLog($"[error] {error.Text}");
                break;

            default:
                foreach (var game in Games)
                    if (game.Apply(message, this))
                        break;
                break;
        }
    }
}
