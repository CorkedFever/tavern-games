using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Games.LiarsDice;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

public sealed class LiarsDiceClient : IClientGame
{
    private static readonly string[] FaceWords =
        { "", "ones", "twos", "threes", "fours", "fives", "sixes" };

    private const float SeatDieSize = 30f;
    private const float PickerDieSize = 22f;
    private const float MarqueeDieSize = 44f;

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
                _bidQuantity = 1;
                session.SetPlayers(round.Players);
                session.AddLog($"New round. {session.NameOf(round.CurrentPlayerId)} bids first.");
                Fx.Roll("liars.table", 0.9); // every cup on the table shakes
                Sound.Dice();
                return true;

            case YourHand hand:
                MyDice = hand.Dice;
                Fx.Roll("liars.mine", 0.9);
                return true;

            case BidPlaced bid:
                CurrentBid = bid.Bid;
                CurrentBidderId = bid.PlayerId;
                CurrentPlayerId = bid.NextPlayerId;
                // The form starts from the standing bid, so raising it is one press, not a hunt.
                if (_bidQuantity < bid.Bid.Quantity) _bidQuantity = bid.Bid.Quantity;
                session.AddLog($"{session.NameOf(bid.PlayerId)} bid {bid.Bid.Quantity} {Face(bid.Bid.FaceValue)}.");
                Fx.Float(bid.PlayerId, $"{bid.Bid.Quantity} {Face(bid.Bid.FaceValue)}", Theme.Accent);
                return true;

            case ChallengeResolved result:
                LastChallenge = result;
                CurrentBid = null; // the bid is settled; nothing to call until the next round
                var verdict = result.BidWasValid ? "the bid held" : "it was a lie";
                session.AddLog(
                    $"{session.NameOf(result.ChallengerId)} called liar on {session.NameOf(result.BidderId)} " +
                    $"({result.Bid.Quantity} {Face(result.Bid.FaceValue)}): there were {result.ActualCount}, so {verdict}. " +
                    $"{session.NameOf(result.LoserId)} loses a die" + (result.LoserEliminated ? " and is out!" : "."));

                // The call is the moment of the game: the verdict stamped on the felt, the hands
                // turned over one after another, and the loser's plate flashing red.
                Fx.StampFelt(result.BidWasValid ? "The bid held" : "Liar!", result.BidWasValid ? Theme.Good : Theme.Bad, 1.6);
                Fx.Deal("liars.reveal");
                Fx.Float(result.LoserId, result.LoserEliminated ? "out!" : "-1 die", Theme.Bad, 0.6);
                Fx.Glow(result.LoserId, Theme.Bad, 1.6);
                Sound.Alert();
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

    // ------------------------------------------------------------------ the display

    public DisplayState Display(GameSession session)
    {
        if (LastChallenge is { } reveal)
            return reveal.BidWasValid ? new("THE BID HELD", Theme.Good) : new("LIAR!", Theme.Bad);
        if (!session.AmActivePlayer)
            return new("YOU'RE OUT", Theme.TextDim);
        if (CurrentPlayerId == session.MyId)
            return new("YOUR TURN", Theme.Accent);
        return new("WAITING", Theme.TextDim);
    }

    // --------------------------------------------------------------------- the felt

    public void DrawSeatTally(PlayerPublic seat)
    {
        if (seat.Tally > 0)
            DiceRenderer.HiddenHand(seat.Tally, 15f, roll: "liars.table");
        else
            ImGui.TextColored(Theme.TextFaint, "no dice");
    }

    public void DrawFelt(GameSession session)
    {
        Felt.Plates(session, DrawSeatTally, CurrentPlayerId);
        ImGui.Dummy(new Vector2(0f, 10f));

        if (LastChallenge is { } reveal)
            DrawReveal(reveal, session);
        else if (CurrentBid is { } bid)
            DrawStandingBid(bid, session);
        else
            Theme.Marquee("No bid yet", Theme.TextFaint, "The opening bid is free.");
    }

    /// <summary>The standing bid, big: the count in the display face and the face as a die.</summary>
    private void DrawStandingBid(BidDto bid, GameSession session)
    {
        var word = $"{bid.Quantity} ×";
        var by = $"by {session.NameOf(CurrentBidderId)}";
        float wordWidth;
        float wordHeight;
        using (Theme.PushDisplayLarge())
        {
            var size = ImGui.CalcTextSize(word);
            wordWidth = size.X;
            wordHeight = size.Y;
        }

        var byWidth = ImGui.CalcTextSize(by).X;
        Ui.CenterNext(wordWidth + 12f + MarqueeDieSize + 12f + byWidth);

        var top = ImGui.GetCursorPosY();
        using (Theme.PushDisplayLarge())
        {
            ImGui.SetCursorPosY(top + MathF.Max(0f, (MarqueeDieSize - wordHeight) / 2f));
            ImGui.TextColored(Theme.Accent, word);
        }
        ImGui.SameLine(0f, 12f);
        ImGui.SetCursorPosY(top);
        DiceRenderer.Die(bid.FaceValue, MarqueeDieSize);
        ImGui.SameLine(0f, 12f);
        ImGui.SetCursorPosY(top + (MarqueeDieSize - ImGui.GetTextLineHeight()) / 2f);
        ImGui.TextColored(Theme.TextDim, by);

        ImGui.Dummy(new Vector2(0f, 4f));
        var hint = "Raise it: more dice, or the same count of a higher face. Or call liar.";
        Ui.CenterNext(ImGui.CalcTextSize(hint).X);
        ImGui.TextColored(Theme.TextFaint, hint);
    }

    /// <summary>After a call: the verdict, then every hand face-up with the dice that counted rimmed in gold.</summary>
    private static void DrawReveal(ChallengeResolved reveal, GameSession session)
    {
        var bid = $"{reveal.Bid.Quantity} {Face(reveal.Bid.FaceValue)}";
        Theme.Marquee(
            reveal.BidWasValid ? "The bid held" : "Liar!",
            reveal.BidWasValid ? Theme.Good : Theme.Bad,
            $"{session.NameOf(reveal.ChallengerId)} called {session.NameOf(reveal.BidderId)} on {bid}: there were {reveal.ActualCount}.");

        var fate = $"{session.NameOf(reveal.LoserId)} loses a die" + (reveal.LoserEliminated ? " and is out!" : ".");
        Ui.CenterNext(ImGui.CalcTextSize(fate).X);
        ImGui.TextColored(Theme.Accent, fate);
        ImGui.Dummy(new Vector2(0f, 6f));

        // The cups come off one at a time, top to bottom.
        for (var i = 0; i < reveal.Reveal.Length; i++)
        {
            var hand = reveal.Reveal[i];
            var shown = Fx.DealProgress("liars.reveal", i);
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.1f + 0.9f * shown);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, Ui.Fit(session.NameOf(hand.PlayerId), 120f));
            ImGui.SameLine(136f);
            if (hand.Dice.Length == 0)
                ImGui.TextColored(Theme.TextFaint, "no dice");
            else
                DiceRenderer.Hand(hand.Dice, 22f, highlightFace: shown >= 1f ? reveal.Bid.FaceValue : 0);

            ImGui.PopStyleVar();
        }
    }

    // --------------------------------------------------------------------- the seat

    public void DrawSeat(GameSession session, Action<NetMessage> send)
    {
        Theme.Heading("Your dice");
        if (MyDice.Length == 0)
        {
            Ui.Hint("The dice are still rolling.");
        }
        else
        {
            // Five dice fit at full size; a bigger hand shrinks to stay on one line.
            var size = MathF.Min(SeatDieSize, (Ui.Avail() - (MyDice.Length - 1) * 4f) / MyDice.Length);
            DiceRenderer.Hand(MyDice, size, gap: 4f, roll: "liars.mine");
        }

        var betweenRounds = LastChallenge is not null;
        var myTurn = session.AmActivePlayer && CurrentPlayerId == session.MyId;

        if (betweenRounds)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("The next round is about to begin.");
            return;
        }

        if (!session.AmActivePlayer)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("You're out of dice. Stay and watch the rest.");
            return;
        }

        if (myTurn)
        {
            DrawBidControls(session, send);
        }
        else
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint($"Waiting for {session.NameOf(CurrentPlayerId)}.");
        }

        // Calling liar is open: any time there's a standing bid you didn't make.
        var canCall = CurrentBid is not null && CurrentBidderId != session.MyId;
        var tip = CurrentBid is { } standing
            ? canCall
                ? $"{session.NameOf(CurrentBidderId)} bid {standing.Quantity} {Face(standing.FaceValue)}. Call it and every cup comes off."
                : "You can't call your own bid."
            : "Nothing to call until someone bids.";
        ImGui.Dummy(new Vector2(0f, 4f));
        if (Ui.Key("Call liar!", 0f, KeyStyle.Bad, canCall, tip))
            send(new Challenge());
    }

    private void DrawBidControls(GameSession session, Action<NetMessage> send)
    {
        Theme.Heading("Your bid");

        var totalDice = Math.Max(1, session.Players.Sum(p => p.Tally));
        _bidQuantity = Math.Clamp(_bidQuantity, 1, totalDice);
        Ui.Stepper("qty", ref _bidQuantity, 1, totalDice, _bidQuantity == 1 ? "die" : "dice");

        // The face is picked by pressing the die that shows it.
        var gap = MathF.Max(2f, (Ui.Avail() - 6f * PickerDieSize) / 5f);
        for (var face = 1; face <= 6; face++)
        {
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##face{face}", new Vector2(PickerDieSize, PickerDieSize)))
                _bidFace = face;
            Ui.Tip(Face(face));
            ImGui.SetCursorScreenPos(pos);
            DiceRenderer.Die(face, PickerDieSize, highlight: face == _bidFace);
            if (face < 6)
                ImGui.SameLine(0f, gap);
        }

        var proposed = new Bid(_bidQuantity, _bidFace);
        var legal = proposed.IsValidShape &&
                    (CurrentBid is not { } cur || proposed.IsHigherThan(cur.ToBid()));

        ImGui.Dummy(new Vector2(0f, 2f));
        if (Ui.Key($"Bid {_bidQuantity} {Face(_bidFace)}", 0f, legal ? KeyStyle.Lit : KeyStyle.Plain, legal,
                legal ? "Place the bid." : "A bid has to raise the standing one: more dice, or the same count of a higher face."))
            send(new PlaceBid(_bidQuantity, _bidFace));
    }

    private static string Face(int face) => face is >= 1 and <= 6 ? FaceWords[face] : $"{face}s";
}
