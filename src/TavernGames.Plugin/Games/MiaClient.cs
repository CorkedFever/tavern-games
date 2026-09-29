using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Games.Mia;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

public sealed class MiaClient : IClientGame
{
    private const float DieSize = 30f;
    private const float RevealDieSize = 24f;
    private const float PickerWidth = 40f;

    public string GameType => MiaModule.Type;
    public string CurrentPlayerId => Table?.CurrentPlayerId ?? "";

    /// <summary>The latest table the server sent. Every Mia message carries a full one.</summary>
    public MiaTable? Table { get; private set; }

    /// <summary>My own dice, as a two-digit code, for as long as the cup is mine. 0 otherwise.</summary>
    public int MyRoll { get; private set; }

    /// <summary>The last call, kept until the next round so the reveal stays readable.</summary>
    public MiaCalled? LastCall { get; private set; }

    /// <summary>The last concession, kept for the same reason. Its dice were never shown.</summary>
    public MiaConceded? LastConcession { get; private set; }

    public void Reset()
    {
        Table = null;
        MyRoll = 0;
        LastCall = null;
        LastConcession = null;
    }

    public bool Apply(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case MiaRoundStarted m:
                MyRoll = 0;
                LastCall = null;
                LastConcession = null;
                SetTable(m.Table, session);
                session.AddLog($"New round. {session.NameOf(m.PlayerId)} takes the cup.");
                return true;

            case MiaYourRoll m:
                // Private, and the only place this value ever appears on a client.
                MyRoll = m.Value;
                Fx.Roll("mia.mine", 0.8);
                return true;

            case MiaRolled m:
                SetTable(m.Table, session);
                session.AddLog(m.PlayerId == session.MyId && MyRoll != 0
                    ? $"You shake the cup and look: {Describe(MyRoll)}."
                    : $"{session.NameOf(m.PlayerId)} shakes the cup and looks.");
                Fx.Roll("mia.cup", 0.8);
                Sound.Dice();
                return true;

            case MiaAnnounced m:
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)} announces {Describe(m.Value)}.");
                Fx.Float(m.PlayerId, Describe(m.Value), Theme.Accent);
                return true;

            case MiaBelieved m:
                MyRoll = 0; // the cup changes hands; whatever was under it is gone
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)} believes {Describe(m.Value)} and takes the cup.");
                return true;

            case MiaCalled m:
                LastCall = m;
                SetTable(m.Table, session);
                session.AddLog(
                    $"{session.NameOf(m.CallerId)} calls liar on {Describe(m.Announced)}: the cup held {Describe(m.Revealed)}. " +
                    $"{session.NameOf(m.LoserId)} loses {Lives(m.LivesLost)}" + (m.LoserEliminated ? " and is out!" : "."));

                // The cup comes off: the dice tumble out, and the verdict lands with them.
                Fx.Roll("mia.reveal", 0.7);
                Sound.Dice();
                Fx.StampFelt(m.Honest ? "The claim stood" : "Liar!", m.Honest ? Theme.Good : Theme.Bad, 1.6, 0.4);
                Fx.Float(m.LoserId, m.LoserEliminated ? "out!" : m.LivesLost == 1 ? "-1 life" : $"-{m.LivesLost} lives", Theme.Bad, 0.7);
                Fx.Glow(m.LoserId, Theme.Bad, 1.6, 0.4);
                Fx.Cue(Sound.Alert, 0.4);
                return true;

            case MiaConceded m:
                LastConcession = m;
                SetTable(m.Table, session);
                session.AddLog(
                    $"{session.NameOf(m.PlayerId)} will not call the Mia and gives up a life" +
                    (m.Eliminated ? ", which was the last one." : "."));
                Fx.StampFelt("A life paid", Theme.TextDim, 1.2);
                Fx.Float(m.PlayerId, m.Eliminated ? "out!" : "-1 life", Theme.Bad, 0.2);
                Fx.Glow(m.PlayerId, Theme.Bad, 1.2);
                return true;

            case MiaSnapshot m:
                SetTable(m.Table, session);
                return true;

            default:
                return false;
        }
    }

    public string? Narrate(NetMessage message, GameSession session) => message switch
    {
        MiaRoundStarted m => $"The cup goes to {session.NameOf(m.PlayerId)}, and the table settles.",
        MiaYourRoll m => $"You tip the cup toward yourself: {Describe(m.Value)}.",
        MiaRolled m when m.PlayerId != session.MyId =>
            $"{session.NameOf(m.PlayerId)} rattles the cup, peeks under it, and keeps a straight face.",
        MiaAnnounced m => $"\"{Describe(m.Value)},\" says {session.NameOf(m.PlayerId)}.",
        MiaBelieved m => $"{session.NameOf(m.PlayerId)} takes that at its word and reaches for the cup.",
        MiaCalled m =>
            $"\"Liar!\" {session.NameOf(m.CallerId)} lifts the cup: {Describe(m.Revealed)} against {Describe(m.Announced)}. " +
            (m.Honest ? "It was true. " : "A bluff! ") +
            $"{session.NameOf(m.LoserId)} loses {Lives(m.LivesLost)}" + (m.LoserEliminated ? " and is out of the game!" : "."),
        MiaConceded m =>
            $"{session.NameOf(m.PlayerId)} will not touch a Mia and pays a life instead" +
            (m.Eliminated ? ", their last." : "."),
        _ => null,
    };

    // ------------------------------------------------------------------ the display

    public DisplayState Display(GameSession session)
    {
        if (LastCall is { } call)
            return call.Honest ? new("THE CLAIM STOOD", Theme.Good) : new("LIAR!", Theme.Bad);
        if (LastConcession is not null)
            return new("A LIFE PAID", Theme.TextDim);
        if (!session.AmActivePlayer)
            return new("YOU'RE OUT", Theme.TextDim);
        if (Table is { } table && table.CurrentPlayerId == session.MyId)
        {
            if (table.Step == MiaStep.Respond && MiaValue.TryFromCode(table.Announced, out var announced) && announced.IsMia)
                return new("MIA!", Theme.Bad);
            return new("YOUR TURN", Theme.Accent);
        }
        return new("WAITING", Theme.TextDim);
    }

    // --------------------------------------------------------------------- the felt

    public void DrawSeatTally(PlayerPublic seat)
    {
        var color = seat.Tally <= 1 ? Theme.Bad : seat.Tally == 2 ? Theme.Accent : Theme.TextDim;
        ImGui.TextColored(color, seat.Tally == 1 ? "1 life" : $"{seat.Tally} lives");
    }

    public void DrawFelt(GameSession session)
    {
        if (Table is not { } table)
        {
            Theme.Marquee("Dealing", Theme.TextFaint, "Waiting for the table.");
            return;
        }

        Felt.Plates(session, DrawSeatTally, table.CurrentPlayerId);
        ImGui.Dummy(new Vector2(0f, 10f));

        if (LastCall is { } call) DrawReveal(call, session);
        else if (LastConcession is { } conceded) DrawConcession(conceded, session);
        else DrawStanding(table, session);
    }

    /// <summary>The claim on the table, big, with the two dice it names, and whose cup it is under.</summary>
    private void DrawStanding(MiaTable table, GameSession session)
    {
        if (MiaValue.TryFromCode(table.Announced, out var announced))
        {
            CenteredPair(announced, DieSize);
            Theme.Marquee(announced.Describe(), announced.IsMia ? Theme.Bad : Theme.Accent,
                $"{session.NameOf(table.AnnouncerId ?? "")} announces. {session.NameOf(table.CurrentPlayerId ?? "")} decides.");
        }
        else if (MiaValue.TryFromCode(table.Accepted, out var accepted))
        {
            Theme.Marquee(accepted.Describe(), Theme.TextDim,
                $"was believed. {session.NameOf(table.CurrentPlayerId ?? "")} has to announce something higher.");
        }
        else
        {
            Theme.Marquee("A fresh round", Theme.TextFaint, "The first announcement can be anything.");
        }

        var owner = CupOwner(table);
        if (owner.Length == 0)
            return;

        ImGui.Dummy(new Vector2(0f, 8f));
        var caption = owner == session.MyId ? "Under your cup (see your seat)" : $"Under {session.NameOf(owner)}'s cup";
        Ui.CenterNext(ImGui.CalcTextSize(caption).X);
        ImGui.TextColored(Theme.TextFaint, caption);
        Ui.CenterNext(RevealDieSize * 2f + 5f);
        DiceRenderer.HiddenHand(2, RevealDieSize, gap: 5f, roll: "mia.cup");
    }

    private static void DrawReveal(MiaCalled call, GameSession session)
    {
        Theme.Marquee(call.Honest ? "The claim stood" : "Liar!", call.Honest ? Theme.Good : Theme.Bad,
            $"{session.NameOf(call.CallerId)} called liar on {session.NameOf(call.AnnouncerId)}.");
        ImGui.Dummy(new Vector2(0f, 6f));

        LabelledPair("Said", call.Announced);
        LabelledPair("Held", call.Revealed, "mia.reveal");

        ImGui.Dummy(new Vector2(0f, 4f));
        var fate = $"{session.NameOf(call.LoserId)} loses {Lives(call.LivesLost)}" + (call.LoserEliminated ? " and is out!" : ".");
        Ui.CenterNext(ImGui.CalcTextSize(fate).X);
        ImGui.TextColored(Theme.Accent, fate);
    }

    private static void DrawConcession(MiaConceded conceded, GameSession session)
    {
        Theme.Marquee("A life paid", Theme.TextDim,
            $"{session.NameOf(conceded.PlayerId)} will not call {session.NameOf(conceded.AnnouncerId)}'s Mia.");
        ImGui.Dummy(new Vector2(0f, 6f));

        LabelledPair("Said", conceded.Announced);

        Ui.CenterNext(60f + RevealDieSize * 2f + 5f + 10f + ImGui.CalcTextSize("never shown").X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextFaint, "Held:");
        ImGui.SameLine(0f, 60f - ImGui.CalcTextSize("Held:").X);
        DiceRenderer.HiddenDie(RevealDieSize);
        ImGui.SameLine(0f, 5f);
        DiceRenderer.HiddenDie(RevealDieSize);
        ImGui.SameLine(0f, 10f);
        ImGui.TextColored(Theme.TextFaint, "never shown");

        ImGui.Dummy(new Vector2(0f, 4f));
        var fate = $"{session.NameOf(conceded.PlayerId)} loses a life" + (conceded.Eliminated ? " and is out!" : ".");
        Ui.CenterNext(ImGui.CalcTextSize(fate).X);
        ImGui.TextColored(Theme.Accent, fate);
    }

    // --------------------------------------------------------------------- the seat

    public void DrawSeat(GameSession session, Action<NetMessage> send)
    {
        if (Table is not { } table)
        {
            Ui.Hint("Waiting for the table.");
            return;
        }

        Theme.Heading("Under your cup");
        if (CupOwner(table) == session.MyId && MiaValue.TryFromCode(MyRoll, out var mine))
        {
            var top = ImGui.GetCursorPosY();
            DrawPair(mine, DieSize, "mia.mine");
            ImGui.SameLine(0f, 10f);
            ImGui.SetCursorPosY(top + (DieSize - ImGui.GetTextLineHeight()) / 2f);
            ImGui.TextColored(Theme.Accent, mine.Describe());
        }
        else
        {
            Ui.Hint("The cup isn't yours right now.");
        }

        if (table.Step == MiaStep.None)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("The next round is about to begin.");
            return;
        }

        if (!session.AmActivePlayer)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("You're out of lives. Stay and watch the rest.");
            return;
        }

        if (table.CurrentPlayerId != session.MyId)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint($"Waiting for {session.NameOf(table.CurrentPlayerId ?? "")}.");
            return;
        }

        if (table.Step == MiaStep.Announce) DrawAnnouncePicker(table, send);
        else DrawAnswer(table, session, send);
    }

    /// <summary>Every legal claim, in rank order, and a one-press way to just tell the truth.</summary>
    private void DrawAnnouncePicker(MiaTable table, Action<NetMessage> send)
    {
        Theme.Heading("Announce");

        MiaValue? floor = MiaValue.TryFromCode(table.Accepted, out var accepted) ? accepted : null;

        if (MiaValue.TryFromCode(MyRoll, out var truth))
        {
            var truthIsLegal = floor is not { } value || truth.Beats(value);
            var label = truth.IsMia ? "Truth: Mia" : $"Truth: {truth.Code}";
            if (Ui.Key(label, 0f, truthIsLegal ? KeyStyle.Lit : KeyStyle.Plain, truthIsLegal,
                    truthIsLegal ? $"Announce {truth.Describe()}, which is what you have." : $"Your throw does not beat {floor?.Describe()}. You'll have to bluff."))
                send(new MiaAnnounce(truth.Code));
        }

        Ui.Hint(floor is { } shown ? $"Or claim anything above {shown.Describe()}:" : "Or claim anything:");

        // The ladder in its three bands, so the ranking is visible at a glance instead of
        // being something you have to remember.
        var legal = MiaValue.Ordered.Where(v => floor is not { } value || v.Beats(value)).ToList();
        DrawPickerBand("Mixed", legal.Where(v => !v.IsDouble && !v.IsMia), send);
        DrawPickerBand("Doubles", legal.Where(v => v.IsDouble), send);
        DrawPickerBand("", legal.Where(v => v.IsMia), send);
    }

    /// <summary>One band of the ladder, wrapped to the seat.</summary>
    private static void DrawPickerBand(string label, IEnumerable<MiaValue> values, Action<NetMessage> send)
    {
        var band = values.ToList();
        if (band.Count == 0) return;

        if (label.Length > 0) ImGui.TextColored(Theme.TextFaint, label);

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var columns = Math.Clamp((int)((Ui.Avail() + spacing) / (PickerWidth + spacing)), 2, 7);
        for (var i = 0; i < band.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine();
            if (ImGui.Button($"{Label(band[i])}##say{band[i].Code}", new Vector2(PickerWidth, 0)))
                send(new MiaAnnounce(band[i].Code));
            Ui.Tip(band[i].Describe());
        }
    }

    /// <summary>Buttons stay narrow, so a double is its two digits and the tooltip spells it out.</summary>
    private static string Label(MiaValue value) => value.IsMia ? "Mia" : value.Code.ToString();

    private static void DrawAnswer(MiaTable table, GameSession session, Action<NetMessage> send)
    {
        Theme.Heading("Your call");

        if (!MiaValue.TryFromCode(table.Announced, out var announced))
        {
            Ui.Hint("Waiting for an announcement.");
            return;
        }

        var facingMia = announced.IsMia;
        Ui.Hint($"{session.NameOf(table.AnnouncerId ?? "")} says {announced.Describe()}. Do you buy it?");

        // Believing a Mia is impossible rather than merely unwise, so the key stays where it is
        // and goes dead: the shape of the column never jumps around.
        if (Ui.Key("Believe it", 0f, facingMia ? KeyStyle.Plain : KeyStyle.Lit, !facingMia,
                facingMia ? "Nothing beats Mia. Call it, or pay a life." : $"Take the cup. You'll have to announce better than {announced.Describe()}."))
            send(new MiaBelieve());

        if (Ui.Key("Call liar!", 0f, KeyStyle.Bad, true,
                facingMia ? "Lift the cup. If it really is Mia, you lose two lives." : "Lift the cup. Whoever was wrong loses a life."))
            send(new MiaCallLiar());

        if (facingMia && Ui.Key("Concede a life", 0f, KeyStyle.Plain, true, "Pay one life rather than risk two."))
            send(new MiaConcede());
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Whose dice are under the cup: the seat about to announce, or the one who just did.</summary>
    private static string CupOwner(MiaTable table) => table.Step switch
    {
        MiaStep.Announce => table.CurrentPlayerId ?? "",
        MiaStep.Respond => table.AnnouncerId ?? "",
        _ => "",
    };

    /// <summary>Keeps the shared roster's life counts in step with the table.</summary>
    private void SetTable(MiaTable table, GameSession session)
    {
        Table = table;
        if (CupOwner(table) != session.MyId) MyRoll = 0;

        session.SetPlayers(session.Players
            .Select(p => table.Seats.FirstOrDefault(s => s.PlayerId == p.Id) is { } seat
                ? p with { Tally = seat.Lives, Eliminated = seat.Out }
                : p)
            .ToArray());
    }

    private static void LabelledPair(string label, int code, string? roll = null)
    {
        var text = MiaValue.TryFromCode(code, out var value) ? value.Describe() : "nothing";
        Ui.CenterNext(60f + RevealDieSize * 2f + 5f + 10f + ImGui.CalcTextSize(text).X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextFaint, label + ":");
        ImGui.SameLine(0f, 60f - ImGui.CalcTextSize(label + ":").X);
        if (MiaValue.TryFromCode(code, out var pair))
        {
            DrawPair(pair, RevealDieSize, roll);
            ImGui.SameLine(0f, 10f);
            ImGui.TextColored(Theme.TextDim, text);
        }
        else
        {
            ImGui.TextColored(Theme.TextFaint, text);
        }
    }

    private static void CenteredPair(MiaValue value, float size)
    {
        Ui.CenterNext(size * 2f + 5f);
        DrawPair(value, size);
    }

    /// <summary>The two dice, high first; a Mia's one is rimmed in gold. Tumbling, if a roll under the key is playing.</summary>
    private static void DrawPair(MiaValue value, float size, string? roll = null) =>
        DiceRenderer.Hand([value.High, value.Low], size, gap: 5f, highlightFace: value.IsMia ? 1 : 0, roll: roll);

    private static string Describe(int code) =>
        MiaValue.TryFromCode(code, out var value) ? value.Describe() : "nothing";

    private static string Lives(int count) => count == 1 ? "a life" : $"{count} lives";
}
