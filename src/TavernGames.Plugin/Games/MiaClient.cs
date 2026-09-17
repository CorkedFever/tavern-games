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
    private const float PickerWidth = 46f;

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
                return true;

            case MiaRolled m:
                SetTable(m.Table, session);
                session.AddLog(m.PlayerId == session.MyId && MyRoll != 0
                    ? $"You shake the cup and look: {Describe(MyRoll)}."
                    : $"{session.NameOf(m.PlayerId)} shakes the cup and looks.");
                return true;

            case MiaAnnounced m:
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)} announces {Describe(m.Value)}.");
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
                return true;

            case MiaConceded m:
                LastConcession = m;
                SetTable(m.Table, session);
                session.AddLog(
                    $"{session.NameOf(m.PlayerId)} will not call the Mia and gives up a life" +
                    (m.Eliminated ? ", which was the last one." : "."));
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

    public void DrawSeatTally(PlayerPublic seat)
    {
        var color = seat.Tally <= 1 ? TableUi.Red : seat.Tally == 2 ? TableUi.Gold : TableUi.Grey;
        ImGui.TextColored(color, seat.Tally == 1 ? "1 life" : $"{seat.Tally} lives");
    }

    public void DrawTable(GameSession session, Action<NetMessage> send)
    {
        if (Table is not { } table)
        {
            ImGui.TextDisabled("Waiting for the table...");
            return;
        }

        TableUi.Roster(session);
        ImGui.Separator();

        if (LastCall is { } call) DrawReveal(call, session);
        else if (LastConcession is { } conceded) DrawConcession(conceded, session);
        else DrawStanding(table, session);

        ImGui.Separator();
        DrawControls(table, session, send);
    }

    /// <summary>The claim on the table, big, with the two dice it names.</summary>
    private void DrawStanding(MiaTable table, GameSession session)
    {
        if (MiaValue.TryFromCode(table.Announced, out var announced))
        {
            ImGui.TextColored(TableUi.Gold, $"{session.NameOf(table.AnnouncerId ?? "")} announces");
            DrawPair(announced, DieSize);
            ImGui.SameLine(0, 12);
            ImGui.TextColored(announced.IsMia ? TableUi.Red : TableUi.Cyan, announced.Describe());
        }
        else if (MiaValue.TryFromCode(table.Accepted, out var accepted))
        {
            ImGui.TextDisabled($"{accepted.Describe()} was believed.");
            ImGui.TextUnformatted($"{session.NameOf(table.CurrentPlayerId ?? "")} has to announce something higher.");
        }
        else
        {
            ImGui.TextDisabled("A fresh round: the first announcement can be anything.");
        }

        DrawCup(table, session);
    }

    /// <summary>The dice nobody but their owner can see: face up for me, face down for everyone else.</summary>
    private void DrawCup(MiaTable table, GameSession session)
    {
        var owner = CupOwner(table);
        if (owner.Length == 0) return;

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();

        if (owner == session.MyId && MiaValue.TryFromCode(MyRoll, out var mine))
        {
            ImGui.TextUnformatted("Under your cup:");
            ImGui.SameLine(0, 8);
            DrawPair(mine, DieSize);
            ImGui.SameLine(0, 10);
            ImGui.TextColored(TableUi.Cyan, mine.Describe());
            return;
        }

        ImGui.TextUnformatted($"Under {session.NameOf(owner)}'s cup:");
        ImGui.SameLine(0, 8);
        DiceRenderer.HiddenDie(DieSize);
        ImGui.SameLine(0, 5);
        DiceRenderer.HiddenDie(DieSize);
    }

    private static void DrawReveal(MiaCalled call, GameSession session)
    {
        ImGui.TextColored(TableUi.Gold,
            $"{session.NameOf(call.CallerId)} called liar on {session.NameOf(call.AnnouncerId)}.");
        ImGui.Spacing();

        DrawLabelledPair("Said", call.Announced);
        DrawLabelledPair("Held", call.Revealed);

        ImGui.TextColored(call.Honest ? TableUi.TurnGreen : TableUi.Red,
            call.Honest ? "The claim stood up." : "A bluff, and a bad one.");
        ImGui.TextColored(TableUi.Gold,
            $"{session.NameOf(call.LoserId)} loses {Lives(call.LivesLost)}" + (call.LoserEliminated ? " and is out!" : "."));
    }

    private static void DrawConcession(MiaConceded conceded, GameSession session)
    {
        ImGui.TextColored(TableUi.Gold,
            $"{session.NameOf(conceded.PlayerId)} conceded {session.NameOf(conceded.AnnouncerId)}'s Mia.");
        ImGui.Spacing();

        DrawLabelledPair("Said", conceded.Announced);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Held:");
        ImGui.SameLine(64);
        DiceRenderer.HiddenDie(RevealDieSize);
        ImGui.SameLine(0, 5);
        DiceRenderer.HiddenDie(RevealDieSize);
        ImGui.SameLine(0, 10);
        ImGui.TextDisabled("never shown");

        ImGui.TextColored(TableUi.Gold,
            $"{session.NameOf(conceded.PlayerId)} loses a life" + (conceded.Eliminated ? " and is out!" : "."));
    }

    private void DrawControls(MiaTable table, GameSession session, Action<NetMessage> send)
    {
        if (session.IsSpectator)
        {
            ImGui.TextDisabled("Watching.");
            return;
        }

        if (table.Step == MiaStep.None)
        {
            ImGui.TextDisabled("The next round is about to begin...");
            return;
        }

        if (!session.AmActivePlayer)
        {
            ImGui.TextDisabled("You are out of lives. Stay and watch the rest.");
            return;
        }

        if (table.CurrentPlayerId != session.MyId)
        {
            ImGui.TextDisabled($"Waiting for {session.NameOf(table.CurrentPlayerId ?? "")}...");
            return;
        }

        if (table.Step == MiaStep.Announce) DrawAnnouncePicker(table, send);
        else DrawAnswer(table, session, send);
    }

    /// <summary>Every legal claim, in rank order, and a one-click way to just tell the truth.</summary>
    private void DrawAnnouncePicker(MiaTable table, Action<NetMessage> send)
    {
        ImGui.TextColored(TableUi.TurnGreen, "Your turn. Announce a value.");

        MiaValue? floor = MiaValue.TryFromCode(table.Accepted, out var accepted) ? accepted : null;

        if (MiaValue.TryFromCode(MyRoll, out var truth))
        {
            var truthIsLegal = floor is not { } value || truth.Beats(value);
            ImGui.BeginDisabled(!truthIsLegal);
            if (ImGui.Button($"Announce the truth ({truth.Describe()})##miatruth"))
                send(new MiaAnnounce(truth.Code));
            ImGui.EndDisabled();

            if (!truthIsLegal && floor is { } beat)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"your throw does not beat {beat.Describe()}");
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled(floor is { } shown ? $"Or claim anything above {shown.Describe()}:" : "Or claim anything:");

        // The ladder in its three bands, so the ranking is visible at a glance instead of
        // being something you have to remember.
        var legal = MiaValue.Ordered.Where(v => floor is not { } value || v.Beats(value)).ToList();
        DrawPickerBand("Mixed", legal.Where(v => !v.IsDouble && !v.IsMia), send);
        DrawPickerBand("Doubles", legal.Where(v => v.IsDouble), send);
        DrawPickerBand("", legal.Where(v => v.IsMia), send);
    }

    /// <summary>One band of the ladder, wrapped to however wide the window happens to be.</summary>
    private static void DrawPickerBand(string label, IEnumerable<MiaValue> values, Action<NetMessage> send)
    {
        var band = values.ToList();
        if (band.Count == 0) return;

        if (label.Length > 0) ImGui.TextDisabled(label);

        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / (PickerWidth + 8f)), 3, 7);
        for (var i = 0; i < band.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine();
            if (ImGui.Button($"{Label(band[i])}##say{band[i].Code}", new Vector2(PickerWidth, 0)))
                send(new MiaAnnounce(band[i].Code));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(band[i].Describe());
        }
    }

    /// <summary>Buttons stay narrow, so a double is its two digits and the tooltip spells it out.</summary>
    private static string Label(MiaValue value) => value.IsMia ? "Mia" : value.Code.ToString();

    private static void DrawAnswer(MiaTable table, GameSession session, Action<NetMessage> send)
    {
        if (!MiaValue.TryFromCode(table.Announced, out var announced))
        {
            ImGui.TextDisabled("Waiting for an announcement...");
            return;
        }

        var facingMia = announced.IsMia;
        ImGui.TextColored(TableUi.TurnGreen,
            $"{session.NameOf(table.AnnouncerId ?? "")} says {announced.Describe()}. Do you buy it?");

        // Believing a Mia is impossible rather than merely unwise, so the button stays
        // where it is and goes dead: the shape of the row never jumps around.
        ImGui.BeginDisabled(facingMia);
        if (ImGui.Button("Believe it##miabelieve"))
            send(new MiaBelieve());
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Call liar!##miacall"))
            send(new MiaCallLiar());

        if (facingMia)
        {
            ImGui.SameLine();
            if (ImGui.Button("Concede a life##miaconcede"))
                send(new MiaConcede());
            ImGui.TextDisabled("Nothing beats Mia. Call it and risk two lives, or give up one.");
        }
        else
        {
            ImGui.TextDisabled($"Believe it and you have to announce better than {announced.Describe()}.");
        }
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

    private static void DrawLabelledPair(string label, int code)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label + ":");
        ImGui.SameLine(64);
        if (MiaValue.TryFromCode(code, out var value))
        {
            DrawPair(value, RevealDieSize);
            ImGui.SameLine(0, 10);
            ImGui.TextDisabled(value.Describe());
        }
        else
        {
            ImGui.TextDisabled("nothing");
        }
    }

    private static void DrawPair(MiaValue value, float size)
    {
        DiceRenderer.Die(value.High, size);
        ImGui.SameLine(0, 5);
        DiceRenderer.Die(value.Low, size, highlight: value.IsMia);
    }

    private static string Describe(int code) =>
        MiaValue.TryFromCode(code, out var value) ? value.Describe() : "nothing";

    private static string Lives(int count) => count == 1 ? "a life" : $"{count} lives";
}
