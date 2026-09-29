using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

public sealed class PigClient : IClientGame
{
    private const float MarqueeDieSize = 44f;

    public string GameType => PigModule.Type;
    public string CurrentPlayerId { get; private set; } = "";

    public int TurnTotal { get; private set; }
    public int TargetScore { get; private set; } = PigGame.DefaultTargetScore;

    // The most recent roll, shown until the next one so a bust stays visible for a moment.
    public int LastFace { get; private set; }
    public bool LastBusted { get; private set; }
    public string LastRollerId { get; private set; } = "";

    /// <summary>Each seat's score bar as drawn, easing toward the real score rather than jumping.</summary>
    private readonly Dictionary<string, float> _shown = new();

    public void Reset()
    {
        CurrentPlayerId = LastRollerId = "";
        TurnTotal = LastFace = 0;
        LastBusted = false;
        TargetScore = PigGame.DefaultTargetScore;
        _shown.Clear();
    }

    public bool Apply(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case PigTurnStarted turn:
                CurrentPlayerId = turn.CurrentPlayerId;
                TurnTotal = turn.TurnTotal;
                TargetScore = turn.TargetScore;
                session.SetPlayers(turn.Players);
                return true;

            case PigRolled roll:
                var lost = roll.Busted ? TurnTotal : 0;
                LastFace = roll.Face;
                LastBusted = roll.Busted;
                LastRollerId = roll.PlayerId;
                TurnTotal = roll.TurnTotal;
                session.AddLog(roll.Busted
                    ? $"{session.NameOf(roll.PlayerId)} rolled a 1 and lost the lot."
                    : $"{session.NameOf(roll.PlayerId)} rolled a {roll.Face}. {roll.TurnTotal} riding.");

                // The die tumbles first; what it means lands once it has settled.
                Fx.Roll("pig", 0.6);
                Sound.Dice();
                if (roll.Busted)
                {
                    Fx.StampFelt("Bust!", Theme.Bad, 1.2, 0.5);
                    Fx.Float(roll.PlayerId, lost > 0 ? $"-{lost}" : "bust", Theme.Bad, 0.5);
                    Fx.Glow(roll.PlayerId, Theme.Bad, 1.4, 0.5);
                    Fx.Cue(Sound.Alert, 0.5);
                }
                else
                {
                    Fx.Float(roll.PlayerId, $"+{roll.Face}", Theme.Text, 0.45);
                }
                return true;

            case PigHeld held:
                TurnTotal = 0;
                session.AddLog($"{session.NameOf(held.PlayerId)} held and banked {held.Banked} (now {held.NewScore}).");
                Fx.Float(held.PlayerId, $"+{held.Banked}", Theme.Good);
                Fx.Fly(held.PlayerId, held.Banked / 6 + 1, Theme.Accent);
                Fx.Glow(held.PlayerId, Theme.Good, 1.2);
                Sound.Chips();
                return true;

            default:
                return false;
        }
    }

    public string? Narrate(NetMessage message, GameSession session) => message switch
    {
        PigRolled { Busted: true } r =>
            $"A one! {session.NameOf(r.PlayerId)} groans as the whole pot slips away.",
        PigRolled r when r.TurnTotal >= 20 =>
            $"{session.NameOf(r.PlayerId)} rolls a {r.Face}. {r.TurnTotal} riding now. Bold.",
        PigRolled r =>
            $"{session.NameOf(r.PlayerId)} rolls a {r.Face}. {r.TurnTotal} riding.",
        PigHeld h =>
            $"{session.NameOf(h.PlayerId)} scoops up {h.Banked} and sits on {h.NewScore}.",
        _ => null,
    };

    // ------------------------------------------------------------------ the display

    public DisplayState Display(GameSession session)
    {
        var myTurn = session.AmActivePlayer && CurrentPlayerId == session.MyId;
        if (LastBusted && LastRollerId == session.MyId && !myTurn)
            return new("BUST!", Theme.Bad);
        return myTurn ? new("YOUR TURN", Theme.Accent) : new("WAITING", Theme.TextDim);
    }

    // --------------------------------------------------------------------- the felt

    public void DrawSeatTally(PlayerPublic seat)
    {
        // The bar eases up to the banked score, so a hold is seen filling it.
        var shown = _shown.GetValueOrDefault(seat.Id, seat.Tally);
        shown += (seat.Tally - shown) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 6f);
        if (MathF.Abs(seat.Tally - shown) < 0.3f) shown = seat.Tally;
        _shown[seat.Id] = shown;

        var fraction = TargetScore > 0 ? Math.Clamp(shown / TargetScore, 0f, 1f) : 0f;
        ImGui.ProgressBar(fraction, new Vector2(Felt.PlateWidth, 0f), $"{seat.Tally} / {TargetScore}");
    }

    public void DrawFelt(GameSession session)
    {
        Felt.Plates(session, DrawSeatTally, CurrentPlayerId);
        ImGui.Dummy(new Vector2(0f, 10f));

        if (LastFace == 0)
        {
            Theme.Marquee("No rolls yet", Theme.TextFaint, $"First to {TargetScore} wins. Roll as often as you dare; a 1 loses the pot.");
            return;
        }

        // The last die rolled, with what it meant beside it.
        var caption = LastBusted
            ? $"Bust! {session.NameOf(LastRollerId)} rolled a 1."
            : $"{session.NameOf(LastRollerId)} rolled a {LastFace}.";
        Ui.CenterNext(MarqueeDieSize + 12f + ImGui.CalcTextSize(caption).X);
        var top = ImGui.GetCursorPosY();
        DiceRenderer.Hand([LastFace], MarqueeDieSize, roll: "pig", rim: LastBusted ? Theme.Bad : null);
        ImGui.SameLine(0f, 12f);
        ImGui.SetCursorPosY(top + (MarqueeDieSize - ImGui.GetTextLineHeight()) / 2f);
        ImGui.TextColored(LastBusted ? Theme.Bad : Theme.TextDim, caption);

        ImGui.Dummy(new Vector2(0f, 4f));
        if (TurnTotal > 0)
            Theme.Marquee($"Riding {TurnTotal}", Theme.Accent, $"{session.NameOf(CurrentPlayerId)} can roll again, or hold and bank it.");
        else
            Theme.Marquee("Nothing riding", Theme.TextFaint, $"{session.NameOf(CurrentPlayerId)} has the die.");
    }

    // --------------------------------------------------------------------- the seat

    public void DrawSeat(GameSession session, Action<NetMessage> send)
    {
        Theme.Heading("Your move");

        var myTurn = session.AmActivePlayer && CurrentPlayerId == session.MyId;
        if (!myTurn)
        {
            Ui.Hint($"Waiting for {session.NameOf(CurrentPlayerId)}.");
            return;
        }

        if (Ui.Key("Roll", 0f, KeyStyle.Lit, true, "Roll one die. A 1 loses everything riding."))
            send(new PigRoll());

        if (Ui.Key(TurnTotal > 0 ? $"Hold {TurnTotal}" : "Hold", 0f, KeyStyle.Plain, TurnTotal > 0,
                TurnTotal > 0 ? $"Bank {TurnTotal} and pass the die." : "Nothing to bank yet: roll first."))
            send(new PigHold());

        var mine = session.Players.FirstOrDefault(p => p.Id == session.MyId)?.Tally ?? 0;
        if (TurnTotal > 0 && mine + TurnTotal >= TargetScore)
        {
            ImGui.Dummy(new Vector2(0f, 2f));
            ImGui.TextColored(Theme.Good, "Hold now and you win!");
        }
    }
}
