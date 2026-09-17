using Dalamud.Bindings.ImGui;
using TavernGames.Core;
using TavernGames.Core.Games.LiarsDice;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

public sealed class LiarsDiceClient : IClientGame
{
    private static readonly string[] FaceWords =
        { "", "ones", "twos", "threes", "fours", "fives", "sixes" };

    // Bid form fields, persisted across frames.
    private int _bidQuantity = 1;
    private int _bidFace = 2;

    public string GameType => LiarsDiceModule.Type;
    public string CurrentPlayerId { get; private set; } = "";

    public int[] MyDice { get; private set; } = [];
    public BidDto? CurrentBid { get; private set; }
    public string CurrentBidderId { get; private set; } = "";

    /// <summary>The last resolved call. Kept until the next round starts so the reveal can be shown.</summary>
    public ChallengeResolved? LastChallenge { get; private set; }

    public void Reset()
    {
        CurrentPlayerId = CurrentBidderId = "";
        MyDice = [];
        CurrentBid = null;
        LastChallenge = null;
        _bidQuantity = 1;
        _bidFace = 2;
    }

    public bool Apply(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case RoundStarted round:
                CurrentPlayerId = round.CurrentPlayerId;
                CurrentBid = null;
                CurrentBidderId = "";
                LastChallenge = null;
                session.SetPlayers(round.Players);
                session.AddLog($"New round. {session.NameOf(round.CurrentPlayerId)} bids first.");
                return true;

            case YourHand hand:
                MyDice = hand.Dice;
                return true;

            case BidPlaced bid:
                CurrentBid = bid.Bid;
                CurrentBidderId = bid.PlayerId;
                CurrentPlayerId = bid.NextPlayerId;
                session.AddLog($"{session.NameOf(bid.PlayerId)} bid {bid.Bid.Quantity} {Face(bid.Bid.FaceValue)}.");
                return true;

            case ChallengeResolved result:
                LastChallenge = result;
                CurrentBid = null; // the bid is settled; nothing to call until the next round
                var verdict = result.BidWasValid ? "the bid held" : "it was a lie";
                session.AddLog(
                    $"{session.NameOf(result.ChallengerId)} called liar on {session.NameOf(result.BidderId)} " +
                    $"({result.Bid.Quantity} {Face(result.Bid.FaceValue)}): there were {result.ActualCount}, so {verdict}. " +
                    $"{session.NameOf(result.LoserId)} loses a die" + (result.LoserEliminated ? " and is out!" : "."));
                return true;

            default:
                return false;
        }
    }

    public string? Narrate(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case RoundStarted r:
                return $"The dice are cupped and rolled. {session.NameOf(r.CurrentPlayerId)} opens the betting.";

            case BidPlaced b:
                return $"\"{b.Bid.Quantity} {Face(b.Bid.FaceValue)},\" declares {session.NameOf(b.PlayerId)}.";

            case ChallengeResolved c:
            {
                var bid = $"{c.Bid.Quantity} {Face(c.Bid.FaceValue)}";
                var verdict = c.BidWasValid
                    ? $"the table holds {c.ActualCount}, and the bid stands!"
                    : $"only {c.ActualCount} show. A bluff!";
                var fate = c.LoserEliminated
                    ? $"{session.NameOf(c.LoserId)} loses their last die and is out of the game!"
                    : $"{session.NameOf(c.LoserId)} loses a die.";
                return $"Liar! {session.NameOf(c.ChallengerId)} doubts {session.NameOf(c.BidderId)} ({bid}): {verdict} {fate}";
            }

            default:
                return null;
        }
    }

    public void DrawSeatTally(PlayerPublic seat)
    {
        if (seat.Tally > 0)
            DiceRenderer.HiddenHand(seat.Tally, 15f);
        else
            ImGui.NewLine();
    }

    public void DrawTable(GameSession session, Action<NetMessage> send)
    {
        TableUi.Roster(session);
        ImGui.Separator();

        if (LastChallenge is { } reveal)
        {
            DrawReveal(reveal, session);
        }
        else if (CurrentBid is { } bid)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Standing bid:");
            ImGui.SameLine();
            ImGui.TextColored(TableUi.Gold, bid.Quantity.ToString());
            ImGui.SameLine(0, 4);
            ImGui.TextDisabled("×");
            ImGui.SameLine(0, 6);
            DiceRenderer.Die(bid.FaceValue, 22f);
            ImGui.SameLine(0, 8);
            ImGui.TextDisabled($"by {session.NameOf(CurrentBidderId)}");
        }
        else
        {
            ImGui.TextDisabled("No bid yet. The opening bid is free.");
        }

        if (!session.IsSpectator)
        {
            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Your dice:");
            ImGui.SameLine(0, 8);
            if (MyDice.Length == 0)
                ImGui.TextDisabled("(none)");
            else
                DiceRenderer.Hand(MyDice, 30f);
        }

        ImGui.Separator();

        var myTurn = session.AmActivePlayer && CurrentPlayerId == session.MyId;
        var betweenRounds = LastChallenge is not null;

        if (betweenRounds)
            ImGui.TextDisabled("Next round is about to begin...");
        else if (myTurn)
            DrawBidControls(send);
        else if (!session.IsSpectator)
            ImGui.TextDisabled($"Waiting for {session.NameOf(CurrentPlayerId)}...");

        // Calling liar is open: available any time there's a standing bid you didn't make.
        var canCall = session.AmActivePlayer && !betweenRounds && CurrentBid is not null && CurrentBidderId != session.MyId;
        if (canCall)
        {
            if (ImGui.Button("Call Liar!"))
                send(new Challenge());
            if (!myTurn)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"(call {session.NameOf(CurrentBidderId)}'s bluff)");
            }
        }
    }

    /// <summary>After a call: every hand face-up, with the dice that counted toward the bid rimmed in gold.</summary>
    private static void DrawReveal(ChallengeResolved reveal, GameSession session)
    {
        var color = reveal.BidWasValid ? TableUi.TurnGreen : TableUi.Red;
        ImGui.TextColored(color,
            $"{session.NameOf(reveal.ChallengerId)} called liar on {reveal.Bid.Quantity} {Face(reveal.Bid.FaceValue)}: " +
            $"there were {reveal.ActualCount}.");
        ImGui.TextColored(TableUi.Gold, $"{session.NameOf(reveal.LoserId)} loses a die" +
                                        (reveal.LoserEliminated ? " and is out!" : "."));
        ImGui.Spacing();

        foreach (var hand in reveal.Reveal)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(session.NameOf(hand.PlayerId));
            ImGui.SameLine(140);
            if (hand.Dice.Length == 0)
                ImGui.TextDisabled("(no dice)");
            else
                DiceRenderer.Hand(hand.Dice, 22f, highlightFace: reveal.Bid.FaceValue);
        }
    }

    private void DrawBidControls(Action<NetMessage> send)
    {
        ImGui.TextColored(TableUi.TurnGreen, "Your turn!");

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Quantity", ref _bidQuantity);
        if (_bidQuantity < 1) _bidQuantity = 1;

        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt("Face", ref _bidFace, Bid.MinFace, Bid.MaxFace);

        var proposed = new Bid(_bidQuantity, _bidFace);
        var legal = proposed.IsValidShape &&
                    (CurrentBid is not { } cur || proposed.IsHigherThan(cur.ToBid()));

        ImGui.BeginDisabled(!legal);
        if (ImGui.Button("Place Bid"))
            send(new PlaceBid(_bidQuantity, _bidFace));
        ImGui.EndDisabled();

        if (!legal)
            ImGui.TextDisabled("Bid must raise the standing bid.");
    }

    private static string Face(int face) => face is >= 1 and <= 6 ? FaceWords[face] : $"{face}s";
}
