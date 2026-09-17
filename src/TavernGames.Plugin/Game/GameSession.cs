using TavernGames.Core;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Game;

/// <summary>
/// The client's view of the game. Mutated only on the main (framework) thread as
/// inbound messages are drained, then read by the UI in the same frame loop — so
/// it needs no locking.
/// </summary>
public sealed class GameSession
{
    public const int MaxLog = 100;

    public string MyId { get; private set; } = "";
    public string RoomCode { get; private set; } = "";
    public string HostId { get; private set; } = "";
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;

    public List<PlayerPublic> Players { get; } = new();
    public int[] MyDice { get; private set; } = [];

    public BidDto? CurrentBid { get; private set; }
    public string CurrentPlayerId { get; private set; } = "";
    public string CurrentBidderId { get; private set; } = "";
    public ChallengeResolved? LastChallenge { get; private set; }
    public string? WinnerId { get; private set; }

    public List<string> Log { get; } = new();

    public bool IsSpectator { get; private set; }

    public bool InRoom => RoomCode.Length > 0;
    public bool IsHost => !IsSpectator && MyId.Length > 0 && MyId == HostId;
    public bool IsMyTurn => !IsSpectator && Phase == GamePhase.Bidding && CurrentPlayerId == MyId;

    /// <summary>I'm a live player still holding dice.</summary>
    public bool AmActivePlayer =>
        !IsSpectator && MyId.Length > 0 && Players.Any(p => p.Id == MyId && !p.Eliminated);

    /// <summary>Open calling: I may call liar on any standing bid that isn't my own.</summary>
    public bool CanCallLiar =>
        AmActivePlayer && Phase == GamePhase.Bidding && CurrentBid is not null && CurrentBidderId != MyId;

    public string NameOf(string id) =>
        Players.FirstOrDefault(p => p.Id == id)?.Name ?? id;

    public void Reset()
    {
        MyId = RoomCode = HostId = CurrentPlayerId = CurrentBidderId = "";
        Phase = GamePhase.Lobby;
        IsSpectator = false;
        Players.Clear();
        MyDice = [];
        CurrentBid = null;
        LastChallenge = null;
        WinnerId = null;
        Log.Clear();
    }

    public void AddLog(string line)
    {
        Log.Add(line);
        if (Log.Count > MaxLog) Log.RemoveRange(0, Log.Count - MaxLog);
    }

    /// <summary>Applies a server message to the local state. Returns false if the message was unhandled.</summary>
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
                Phase = update.Phase;
                ReplacePlayers(update.Players);
                break;

            case RoundStarted round:
                Phase = GamePhase.Bidding;
                CurrentPlayerId = round.CurrentPlayerId;
                CurrentBid = null;
                CurrentBidderId = "";
                LastChallenge = null;
                ReplacePlayers(round.Players);
                AddLog($"New round. {NameOf(round.CurrentPlayerId)} bids first.");
                break;

            case YourHand hand:
                MyDice = hand.Dice;
                break;

            case BidPlaced bid:
                CurrentBid = bid.Bid;
                CurrentBidderId = bid.PlayerId;
                CurrentPlayerId = bid.NextPlayerId;
                AddLog($"{NameOf(bid.PlayerId)} bid {bid.Bid.Quantity}× [{bid.Bid.FaceValue}].");
                break;

            case ChallengeResolved result:
                LastChallenge = result;
                var verdict = result.BidWasValid ? "the bid held" : "it was a lie";
                AddLog($"{NameOf(result.ChallengerId)} called liar on " +
                       $"{NameOf(result.BidderId)} ({result.Bid.Quantity}× [{result.Bid.FaceValue}]) — " +
                       $"there were {result.ActualCount}; {verdict}. " +
                       $"{NameOf(result.LoserId)} loses a die" +
                       (result.LoserEliminated ? " and is out!" : "."));
                if (result.GameOver) WinnerId = result.WinnerId;
                break;

            case GameEnded ended:
                Phase = GamePhase.GameOver;
                WinnerId = ended.WinnerId;
                ReplacePlayers(ended.Players);
                AddLog($"Game over — {NameOf(ended.WinnerId)} wins!");
                break;

            case ErrorMessage error:
                AddLog($"[error] {error.Text}");
                break;
        }
    }

    private void ReplacePlayers(PlayerPublic[] players)
    {
        Players.Clear();
        Players.AddRange(players);
    }
}
