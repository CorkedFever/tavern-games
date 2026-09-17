using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Cards;
using TavernGames.Core.Games.Holdem;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

public sealed class HoldemClient : IClientGame
{
    private const float BoardCardWidth = 40f;
    private const float HoleCardWidth = 30f;
    private const float DetailColumn = 190f;

    /// <summary>The raise-to the slider is sitting on, kept across frames. Re-clamped every draw.</summary>
    private int _raiseTo;

    public string GameType => HoldemModule.Type;
    public string CurrentPlayerId => Table?.CurrentPlayerId ?? "";

    /// <summary>The latest table the server sent. Every hold'em message carries a full one.</summary>
    public HoldemTable? Table { get; private set; }

    /// <summary>My own two cards, which arrive privately and are the one thing the table never shows.</summary>
    public string[] MyCards { get; private set; } = [];

    /// <summary>The hands shown this hand, kept on screen until the next one is dealt.</summary>
    public HoldemReveal[] Showdown { get; private set; } = [];

    public HoldemAward[] LastAwards { get; private set; } = [];

    public void Reset()
    {
        Table = null;
        MyCards = [];
        Showdown = [];
        LastAwards = [];
        _raiseTo = 0;
    }

    public bool Apply(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case HoldemHandStarted m:
                MyCards = [];
                Showdown = [];
                LastAwards = [];
                _raiseTo = 0;
                SetTable(m.Table, session);
                session.AddLog(HandHeadline(m.Table, session));
                return true;

            case HoldemYourCards m:
                MyCards = m.Cards;
                return true;

            case HoldemBlindPosted m:
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)} posts the {(m.Big ? "big" : "small")} blind, {m.Amount}.");
                return true;

            case HoldemActed m:
                session.AddLog(DescribeAction(m, session));
                SetTable(m.Table, session);
                return true;

            case HoldemStreetDealt m:
                SetTable(m.Table, session);
                session.AddLog($"The {StreetName(m.Street)}: {CardList(m.Dealt)}.");
                return true;

            case HoldemShowdown m:
                Showdown = m.Reveals;
                SetTable(m.Table, session);
                foreach (var reveal in m.Reveals)
                    session.AddLog(reveal.Hand is null
                        ? $"{session.NameOf(reveal.PlayerId)} turns over {CardList(reveal.Cards)}."
                        : $"{session.NameOf(reveal.PlayerId)} shows {reveal.Hand}.");
                return true;

            case HoldemHandEnded m:
                LastAwards = m.Awards;
                SetTable(m.Table, session);
                foreach (var award in m.Awards)
                    session.AddLog(DescribeAward(award, session));
                foreach (var id in m.BustedOut)
                    session.AddLog($"{session.NameOf(id)} is out of chips.");
                return true;

            case HoldemSnapshot m:
                SetTable(m.Table, session);
                return true;

            default:
                return false;
        }
    }

    public string? Narrate(NetMessage message, GameSession session) => message switch
    {
        HoldemHandStarted m => HandHeadline(m.Table, session),
        HoldemActed m => DescribeAction(m, session),
        HoldemStreetDealt m => $"The {StreetName(m.Street)} falls: {CardList(m.Dealt)}.",
        HoldemShowdown { Reveals.Length: > 0 } m when m.Reveals[0].Hand is not null =>
            string.Join(" ", m.Reveals.Select(r => $"{session.NameOf(r.PlayerId)} shows {r.Hand}.")),
        HoldemShowdown => "Cards on the table, face up.",
        HoldemHandEnded { Awards.Length: > 0 } m =>
            string.Join(" ", m.Awards.Select(a => DescribeAward(a, session))),
        _ => null,
    };

    public void DrawSeatTally(PlayerPublic seat) => ImGui.TextDisabled($"{seat.Tally} chips");

    public void DrawTable(GameSession session, Action<NetMessage> send)
    {
        if (Table is not { } table)
        {
            ImGui.TextDisabled("Waiting for the table...");
            return;
        }

        DrawHeader(table);
        ImGui.Separator();
        DrawBoard(table);
        ImGui.Separator();

        foreach (var seat in table.Seats)
            DrawSeat(seat, table, session);

        if (Showdown.Length > 0)
        {
            ImGui.Separator();
            DrawShowdown(session);
        }

        ImGui.Separator();
        DrawControls(table, session, send);
    }

    // ------------------------------------------------------------------ the table

    private static void DrawHeader(HoldemTable table)
    {
        ImGui.TextDisabled($"Hand {table.Hand}");
        ImGui.SameLine();
        ImGui.TextColored(TableUi.Gold, $"Blinds {table.SmallBlind}/{table.BigBlind}");
        ImGui.SameLine();
        ImGui.TextDisabled(table.HandsUntilBlindsUp > 0
            ? $"(level {table.BlindLevel}, up in {table.HandsUntilBlindsUp})"
            : "(fixed)");
    }

    private static void DrawBoard(HoldemTable table)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(TableUi.Gold, $"Pot {table.Pot}");
        ImGui.SameLine();
        ImGui.TextDisabled(StreetName(table.Street));

        // The split only means anything once somebody is all-in; before that every street
        // looks briefly uneven while the bets are still being matched.
        if (table.Pots.Length > 1 && table.Seats.Any(s => s.AllIn))
        {
            var parts = table.Pots.Select((p, i) => $"{(i == 0 ? "main" : "side")} {p.Amount}");
            ImGui.TextDisabled(string.Join(", ", parts));
        }

        if (table.Community.Length == 0)
            ImGui.TextDisabled("(no cards on the table yet)");
        else
            CardRenderer.Hand(table.Community, BoardCardWidth);
    }

    private void DrawSeat(HoldemSeat seat, HoldemTable table, GameSession session)
    {
        var isCurrent = seat.PlayerId == table.CurrentPlayerId;
        var isMe = seat.PlayerId == session.MyId;
        var isBot = session.Players.FirstOrDefault(p => p.Id == seat.PlayerId)?.IsBot == true;
        var tag = isMe ? " (you)" : isBot ? " (bot)" : "";

        ImGui.TextColored(isCurrent ? TableUi.TurnGreen : TableUi.Grey, isCurrent ? ">>" : "  ");
        ImGui.SameLine(0, 4);
        ImGui.TextColored(
            seat.Eliminated ? TableUi.Grey : isCurrent ? TableUi.TurnGreen : Vector4.One,
            session.NameOf(seat.PlayerId) + tag);

        if (seat.PlayerId == table.ButtonId)
        {
            ImGui.SameLine(0, 6);
            ImGui.TextColored(TableUi.Gold, "[D]");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(SeatStatus(seat));

        // My own cards are face up for me; everyone else shows whatever the table allows.
        var codes = isMe && MyCards.Length > 0 && seat.Cards.Length > 0 ? MyCards : seat.Cards;
        if (codes.Length == 0) return;

        CardRenderer.Hand(codes, HoleCardWidth);

        if (Showdown.FirstOrDefault(r => r.PlayerId == seat.PlayerId)?.Hand is { } hand)
        {
            ImGui.SameLine(0, 10);
            ImGui.TextColored(WonSomething(seat.PlayerId) ? TableUi.TurnGreen : TableUi.Cyan, hand);
        }
    }

    private void DrawShowdown(GameSession session)
    {
        ImGui.TextColored(TableUi.Gold, "Showdown");
        foreach (var reveal in Showdown)
        {
            var won = WonSomething(reveal.PlayerId);
            ImGui.TextColored(won ? TableUi.TurnGreen : Vector4.One, session.NameOf(reveal.PlayerId));
            ImGui.SameLine(DetailColumn);
            ImGui.TextUnformatted(reveal.Hand ?? CardList(reveal.Cards));
        }

        foreach (var award in LastAwards)
            ImGui.TextColored(
                award.Kind == HoldemAwardKind.Uncalled ? TableUi.Cyan : TableUi.Gold,
                DescribeAward(award, session));
    }

    // --------------------------------------------------------------- your actions

    private void DrawControls(HoldemTable table, GameSession session, Action<NetMessage> send)
    {
        var me = table.Seats.FirstOrDefault(s => s.PlayerId == session.MyId);
        if (session.IsSpectator || me is null)
        {
            ImGui.TextDisabled("Watching the table.");
            return;
        }

        if (me.Eliminated)
        {
            ImGui.TextDisabled("You're out of chips. You can stay and watch the rest.");
            return;
        }

        if (table.CurrentPlayerId != session.MyId)
        {
            ImGui.TextDisabled(table.CurrentPlayerId is { } current
                ? $"Waiting for {session.NameOf(current)}..."
                : "The dealer is working...");
            return;
        }

        ImGui.TextColored(TableUi.TurnGreen, "Your turn!");

        if (ImGui.Button("Fold##holdemFold"))
            send(new HoldemAct(HoldemMove.Fold));

        ImGui.SameLine();
        if (table.ToCall == 0)
        {
            if (ImGui.Button("Check##holdemCheck"))
                send(new HoldemAct(HoldemMove.Check));
        }
        else
        {
            var allIn = table.ToCall >= me.Chips ? " (all in)" : "";
            if (ImGui.Button($"Call {table.ToCall}{allIn}##holdemCall"))
                send(new HoldemAct(HoldemMove.Call));
        }

        // The raise controls stay on screen when raising is closed to you, greyed out with
        // the reason next to them: a control that vanishes just looks like a missing feature.
        var min = table.MinRaiseTo;
        var max = table.MaxRaiseTo;
        _raiseTo = Math.Clamp(_raiseTo, min, max);

        ImGui.BeginDisabled(!table.CanRaise);

        ImGui.SetNextItemWidth(220);
        ImGui.SliderInt("##holdemRaiseTo", ref _raiseTo, min, max);
        ImGui.SameLine();
        var verb = table.CurrentBet == 0 ? "Bet" : "Raise to";
        if (ImGui.Button($"{verb} {_raiseTo}{(_raiseTo >= max ? " (all in)" : "")}##holdemRaise"))
            send(new HoldemAct(HoldemMove.Raise, _raiseTo));

        if (ImGui.SmallButton("Min##holdemMin")) _raiseTo = min;
        ImGui.SameLine();
        if (ImGui.SmallButton("Half pot##holdemHalfPot")) _raiseTo = PotSized(table, me, 1, 2, min, max);
        ImGui.SameLine();
        if (ImGui.SmallButton("Pot##holdemPot")) _raiseTo = PotSized(table, me, 1, 1, min, max);
        ImGui.SameLine();
        if (ImGui.SmallButton("All in##holdemAllIn")) _raiseTo = max;

        ImGui.EndDisabled();

        if (!table.CanRaise)
            ImGui.TextDisabled("No raise open to you here: call or fold.");
    }

    /// <summary>A raise of a fraction of the pot as it will stand once this call is in.</summary>
    private static int PotSized(HoldemTable table, HoldemSeat me, int numerator, int denominator, int min, int max)
    {
        var potAfterCalling = table.Pot + table.ToCall;
        var raiseTo = me.StreetBet + table.ToCall + potAfterCalling * numerator / denominator;
        return Math.Clamp(raiseTo, min, max);
    }

    // ------------------------------------------------------------------- wording

    /// <summary>Keeps the shared roster's chip counts and knockouts in step with the table.</summary>
    private void SetTable(HoldemTable table, GameSession session)
    {
        Table = table;
        session.SetPlayers(session.Players
            .Select(p => table.Seats.FirstOrDefault(s => s.PlayerId == p.Id) is { } seat
                ? p with { Tally = seat.Chips, Eliminated = seat.Eliminated }
                : p)
            .ToArray());
    }

    private bool WonSomething(string playerId) =>
        LastAwards.Any(a => a.PlayerId == playerId && a.Kind != HoldemAwardKind.Uncalled);

    private static string HandHeadline(HoldemTable table, GameSession session)
    {
        var button = table.ButtonId is { } id ? $" {session.NameOf(id)} has the button." : "";
        return $"Hand {table.Hand}. Blinds {table.SmallBlind}/{table.BigBlind}.{button}";
    }

    private static string DescribeAction(HoldemActed acted, GameSession session)
    {
        var name = session.NameOf(acted.PlayerId);
        var seat = acted.Table.Seats.FirstOrDefault(s => s.PlayerId == acted.PlayerId);
        var allIn = seat is { AllIn: true };

        // Nobody else having chips out in front means this was the first bet of the street
        // rather than a raise over one. Before the flop the blinds are out, so it is a raise.
        var opening = acted.Table.Seats.All(s => s.PlayerId == acted.PlayerId || s.StreetBet == 0);

        return acted.Move switch
        {
            HoldemMove.Fold => $"{name} folds.",
            HoldemMove.Check => $"{name} checks.",
            HoldemMove.Call when allIn => $"{name} calls {acted.Amount} and is all in.",
            HoldemMove.Call => $"{name} calls {acted.Amount}.",
            HoldemMove.Raise when allIn => $"{name} moves all in for {acted.Amount}.",
            HoldemMove.Raise when opening => $"{name} bets {acted.Amount}.",
            _ => $"{name} raises to {acted.Amount}.",
        };
    }

    private static string DescribeAward(HoldemAward award, GameSession session)
    {
        var name = session.NameOf(award.PlayerId);
        var with = award.Hand is null ? "" : $" with {award.Hand}";
        return award.Kind switch
        {
            HoldemAwardKind.Uncalled => $"{award.Amount} goes back to {name}, uncalled.",
            HoldemAwardKind.Side => $"{name} takes the side pot, {award.Amount}{with}.",
            _ => $"{name} takes the pot, {award.Amount}{with}.",
        };
    }

    private static string SeatStatus(HoldemSeat seat)
    {
        if (seat.Eliminated) return "busted";
        if (seat.Folded) return $"folded, {seat.Chips} chips";
        if (seat.AllIn) return $"all in for {seat.Committed}";
        return seat.StreetBet > 0 ? $"{seat.Chips} chips, bet {seat.StreetBet}" : $"{seat.Chips} chips";
    }

    private static string StreetName(HoldemStreet street) => street switch
    {
        HoldemStreet.Preflop => "before the flop",
        HoldemStreet.Flop => "flop",
        HoldemStreet.Turn => "turn",
        HoldemStreet.River => "river",
        _ => "showdown",
    };

    private static string CardList(IReadOnlyList<string> codes) =>
        codes.Count == 0 ? "nothing" : string.Join(", ", codes.Select(Card.NameOf));
}
