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
    private const float HoleCardWidth = 40f;
    private const float PlateCardWidth = 26f;

    /// <summary>The raise-to the slider is sitting on, kept across frames. Re-clamped every draw.</summary>
    private int _raiseTo;

    /// <summary>The pot as drawn, counting up to the real one rather than jumping. Negative until first seen.</summary>
    private float _shownPot = -1f;

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
        _shownPot = -1f;
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

                // Two cards each, round the table from the button's left.
                Sound.Deal();
                var dealt = 0;
                foreach (var seat in m.Table.Seats.Where(s => !s.Eliminated))
                    Fx.Deal($"holdem.{seat.PlayerId}", delay: dealt++ * 0.12);
                return true;

            case HoldemYourCards m:
                MyCards = m.Cards;
                Fx.Deal("holdem.mine", delay: 0.2);
                return true;

            case HoldemBlindPosted m:
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)} posts the {(m.Big ? "big" : "small")} blind, {m.Amount}.");
                Fx.Float(m.PlayerId, $"blind {m.Amount}", Theme.TextDim);
                return true;

            case HoldemActed m:
                session.AddLog(DescribeAction(m, session));
                SetTable(m.Table, session);
                StageAction(m);
                return true;

            case HoldemStreetDealt m:
                _raiseTo = 0; // a new street is a new bet: never carry the last one over
                SetTable(m.Table, session);
                session.AddLog($"The {StreetName(m.Street)}: {CardList(m.Dealt)}.");
                Fx.Deal("holdem.board", from: Math.Max(0, m.Table.Community.Length - m.Dealt.Length));
                Fx.Float("pot", StreetName(m.Street), Theme.TextDim, 0.3);
                Sound.Deal();
                return true;

            case HoldemShowdown m:
                Showdown = m.Reveals;
                SetTable(m.Table, session);
                foreach (var reveal in m.Reveals)
                    session.AddLog(reveal.Hand is null
                        ? $"{session.NameOf(reveal.PlayerId)} turns over {CardList(reveal.Cards)}."
                        : $"{session.NameOf(reveal.PlayerId)} shows {reveal.Hand}.");

                // The hands turn over one seat at a time, so the showdown can be followed.
                Fx.StampFelt("Showdown", Theme.Accent, 1.2);
                for (var i = 0; i < m.Reveals.Length; i++)
                    Fx.Flip($"holdem.{m.Reveals[i].PlayerId}", 0.3 + i * 0.35);
                return true;

            case HoldemHandEnded m:
                LastAwards = m.Awards;
                SetTable(m.Table, session);
                foreach (var award in m.Awards)
                    session.AddLog(DescribeAward(award, session));
                foreach (var id in m.BustedOut)
                    session.AddLog($"{session.NameOf(id)} is out of chips.");
                StageAwards(m, session);
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

    // -------------------------------------------------------------------- the stage

    /// <summary>What a seat did rises off its plate; going all in is a moment for the whole table.</summary>
    private static void StageAction(HoldemActed m)
    {
        var seat = m.Table.Seats.FirstOrDefault(s => s.PlayerId == m.PlayerId);
        var allIn = seat is { AllIn: true };
        var (text, colour) = m.Move switch
        {
            HoldemMove.Fold => ("fold", Theme.TextFaint),
            HoldemMove.Check => ("check", Theme.TextDim),
            HoldemMove.Call when allIn => ("all in", Theme.Accent),
            HoldemMove.Call => ($"call {m.Amount}", Theme.Text),
            HoldemMove.Raise when allIn => ("all in", Theme.Accent),
            _ => ($"{m.Amount}", Theme.Accent),
        };

        Fx.Float(m.PlayerId, text, colour);
        if (allIn)
        {
            Fx.StampFelt("All in!", Theme.Accent, 1.0);
            Fx.Glow(m.PlayerId, Theme.Accent, 1.2);
        }
    }

    /// <summary>The pot crosses the felt to whoever won it, once the showdown has been seen.</summary>
    private void StageAwards(HoldemHandEnded m, GameSession session)
    {
        var delay = Showdown.Length > 0 ? 0.6 + Showdown.Length * 0.35 : 0.1;
        foreach (var award in m.Awards)
        {
            if (award.Kind == HoldemAwardKind.Uncalled)
            {
                Fx.Float(award.PlayerId, $"{award.Amount} back", Theme.TextDim, delay);
                continue;
            }

            Fx.Float(award.PlayerId, $"+{award.Amount}", Theme.Good, delay);
            Fx.Fly(award.PlayerId, award.Amount / 25 + 3, Theme.Accent, delay);
            Fx.Glow(award.PlayerId, Theme.Good, 1.8, delay);
            if (award.PlayerId == session.MyId)
            {
                Fx.StampFelt("You take it", Theme.Good, 1.4, delay + 0.2);
                Fx.Cue(Sound.Chips, delay);
            }
        }

        foreach (var id in m.BustedOut)
        {
            Fx.Float(id, "busted", Theme.Bad, delay + 0.5);
            Fx.Glow(id, Theme.Bad, 1.5, delay + 0.5);
        }
    }

    // ------------------------------------------------------------------ the display

    public DisplayState Display(GameSession session)
    {
        if (Table is not { } table)
            return new("DEALING", Theme.TextDim);

        var me = table.Seats.FirstOrDefault(s => s.PlayerId == session.MyId);
        if (me is null)
            return new("WAITING", Theme.TextDim);
        if (me.Eliminated)
            return new("BUSTED", Theme.TextDim);

        if (LastAwards.Length > 0)
            return WonSomething(session.MyId) ? new("YOU TAKE IT", Theme.Good) : new("HAND OVER", Theme.TextDim);
        if (Showdown.Length > 0)
            return new("SHOWDOWN", Theme.Accent);
        if (table.CurrentPlayerId == session.MyId)
            return new("YOUR TURN", Theme.Accent);
        if (me.Folded)
            return new("FOLDED", Theme.TextDim);
        if (me.AllIn)
            return new("ALL IN", Theme.Accent);
        return new("WAITING", Theme.TextDim);
    }

    // --------------------------------------------------------------------- the felt

    public void DrawSeatTally(PlayerPublic seat) => ImGui.TextColored(Theme.TextDim, $"{seat.Tally} chips");

    public void DrawFelt(GameSession session)
    {
        if (Table is not { } table)
        {
            Theme.Marquee("Dealing", Theme.TextFaint, "Waiting for the table.");
            return;
        }

        DrawBoard(table, session);
        ImGui.Dummy(new Vector2(0f, 10f));

        Felt.Plates(session, DrawSeatTally, table.CurrentPlayerId,
            trailing: p => DrawPlateHand(p, table, session),
            nameLine: p => DrawButton(p, table));

        if (Showdown.Length > 0 || LastAwards.Length > 0)
        {
            ImGui.Dummy(new Vector2(0f, 8f));
            DrawShowdown(session);
        }
    }

    /// <summary>The pot, big, the community cards under it, and the state of the hand in a line.</summary>
    private void DrawBoard(HoldemTable table, GameSession session)
    {
        // The pot counts up to what it holds, so chips can be seen going in.
        if (_shownPot < 0f) _shownPot = table.Pot;
        _shownPot += (table.Pot - _shownPot) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 7f);
        if (MathF.Abs(table.Pot - _shownPot) < 0.6f) _shownPot = table.Pot;
        Theme.Marquee($"Pot {(int)MathF.Round(_shownPot)}", Theme.Accent);
        ImGui.Dummy(new Vector2(0f, 2f));

        // Five slots, dealt or waiting, so the street is visible at a glance.
        const int slots = 5;
        var gap = 6f;
        Ui.CenterNext(slots * BoardCardWidth + (slots - 1) * gap);
        var dl = ImGui.GetWindowDrawList();
        for (var i = 0; i < slots; i++)
        {
            if (i < table.Community.Length)
            {
                var arrived = Fx.DealProgress("holdem.board", i);
                CardRenderer.Draw(table.Community[i], BoardCardWidth, alpha: 0.15f + 0.85f * arrived, lift: (1f - arrived) * 16f);
            }
            else
            {
                var p = ImGui.GetCursorScreenPos();
                var size = new Vector2(BoardCardWidth, CardRenderer.HeightFor(BoardCardWidth));
                dl.AddRect(p, p + size, Theme.U32(Theme.WithAlpha(Theme.Text, 0.12f)), BoardCardWidth * 0.12f, ImDrawFlags.RoundCornersAll, 1f);
                ImGui.Dummy(size);
            }
            if (i < slots - 1)
                ImGui.SameLine(0f, gap);
        }

        var parts = new List<string> { StreetName(table.Street) };
        parts.Add($"hand {table.Hand}, blinds {table.SmallBlind}/{table.BigBlind}" + (table.HandsUntilBlindsUp > 0 ? $" (up in {table.HandsUntilBlindsUp})" : ""));
        if (table.ButtonId is { } button)
            parts.Add($"{session.NameOf(button)} has the button");

        // The split only means anything once somebody is all-in; before that every street
        // looks briefly uneven while the bets are still being matched.
        if (table.Pots.Length > 1 && table.Seats.Any(s => s.AllIn))
            parts.Add(string.Join(", ", table.Pots.Select((p, i) => $"{(i == 0 ? "main" : "side")} {p.Amount}")));

        ImGui.Dummy(new Vector2(0f, 2f));
        var line = string.Join(" · ", parts);
        Ui.CenterNext(ImGui.CalcTextSize(line).X);
        ImGui.TextColored(Theme.TextFaint, line);
    }

    /// <summary>The dealer button, on the plate of whoever holds it.</summary>
    private static void DrawButton(PlayerPublic p, HoldemTable table)
    {
        if (p.Id != table.ButtonId)
            return;
        ImGui.SameLine();
        Theme.Displayed(Theme.Accent, "D");
        Ui.Tip("The dealer button. The blinds sit to its left.");
    }

    /// <summary>A seat's cards on its plate: backs until a showdown shows them, and how the seat stands.</summary>
    private void DrawPlateHand(PlayerPublic p, HoldemTable table, GameSession session)
    {
        var seat = table.Seats.FirstOrDefault(s => s.PlayerId == p.Id);
        if (seat is null || seat.Eliminated)
            return;

        if (seat.Cards.Length > 0 && !seat.Folded)
            CardRenderer.Hand(seat.Cards, PlateCardWidth, gap: 3f, deal: $"holdem.{seat.PlayerId}", flip: $"holdem.{seat.PlayerId}");

        ImGui.TextColored(seat.Folded ? Theme.TextFaint : Theme.TextDim, SeatStatus(seat));

        if (Showdown.FirstOrDefault(r => r.PlayerId == seat.PlayerId)?.Hand is { } hand)
            ImGui.TextColored(WonSomething(seat.PlayerId) ? Theme.Good : Theme.Text, Ui.Fit(hand, Felt.PlateWidth));
    }

    private void DrawShowdown(GameSession session)
    {
        if (Showdown.Length > 0)
            Felt.Label("Showdown");

        foreach (var award in LastAwards)
            ImGui.TextColored(award.Kind == HoldemAwardKind.Uncalled ? Theme.TextDim : Theme.Accent, DescribeAward(award, session));
    }

    // --------------------------------------------------------------------- the seat

    public void DrawSeat(GameSession session, Action<NetMessage> send)
    {
        if (Table is not { } table)
        {
            Ui.Hint("Waiting for the table.");
            return;
        }

        var me = table.Seats.FirstOrDefault(s => s.PlayerId == session.MyId);
        if (me is null)
        {
            Ui.Hint("Waiting for a seat.");
            return;
        }

        Theme.Heading("Your cards");
        if (me.Eliminated)
        {
            Ui.Hint("You're out of chips. You can stay and watch the rest.");
            return;
        }

        if (MyCards.Length > 0 && !me.Folded)
        {
            var top = ImGui.GetCursorPosY();
            CardRenderer.Hand(MyCards, HoleCardWidth, deal: "holdem.mine");
            ImGui.SameLine(0f, 10f);
            ImGui.SetCursorPosY(top + 6f);
            ImGui.BeginGroup();
            ImGui.TextColored(Theme.Text, $"{me.Chips} chips");
            ImGui.TextColored(Theme.TextFaint, me.AllIn ? "all in" : table.CurrentPlayerId == session.MyId && table.ToCall > 0 ? $"{table.ToCall} to call" : me.StreetBet > 0 ? $"bet {me.StreetBet}" : "");
            ImGui.EndGroup();
        }
        else
        {
            Ui.Hint(me.Folded ? $"You folded. {me.Chips} chips." : $"No cards yet. {me.Chips} chips.");
        }

        Theme.Heading("Your move");
        if (table.CurrentPlayerId != session.MyId)
        {
            Ui.Hint(table.CurrentPlayerId is { } current
                ? $"Waiting for {session.NameOf(current)}."
                : "The dealer is working.");
            return;
        }

        var half = Ui.HalfKeyWidth();
        if (Ui.Key("Fold", half, KeyStyle.Plain, true, "Throw the hand away. Whatever you put in stays in."))
            send(new HoldemAct(HoldemMove.Fold));

        ImGui.SameLine();
        if (table.ToCall == 0)
        {
            if (Ui.Key("Check", half, KeyStyle.Lit, true, "Nothing to call. Pass the action along."))
                send(new HoldemAct(HoldemMove.Check));
        }
        else
        {
            var allIn = table.ToCall >= me.Chips;
            if (Ui.Key(allIn ? "Call, all in" : $"Call {table.ToCall}", half, KeyStyle.Lit, true, allIn ? $"Match with everything you have, {me.Chips}." : $"Match the bet with {table.ToCall} chips."))
                send(new HoldemAct(HoldemMove.Call));
        }

        // The raise controls stay on screen when raising is closed to you, greyed out with the
        // reason next to them: a control that vanishes just looks like a missing feature.
        var min = table.MinRaiseTo;
        var max = table.MaxRaiseTo;

        // Snap back rather than clamp: a figure left over from a bigger street would otherwise
        // be squeezed down onto the stack and quietly turn the default key into an all-in.
        if (_raiseTo < min || _raiseTo > max) _raiseTo = min;

        ImGui.BeginDisabled(!table.CanRaise);
        ImGui.SetNextItemWidth(Ui.Avail());
        ImGui.SliderInt("##holdemRaiseTo", ref _raiseTo, min, max, "to %d");

        if (ImGui.SmallButton("Min")) _raiseTo = min;
        ImGui.SameLine();
        if (ImGui.SmallButton("½ pot")) _raiseTo = PotSized(table, me, 1, 2, min, max);
        ImGui.SameLine();
        if (ImGui.SmallButton("Pot")) _raiseTo = PotSized(table, me, 1, 1, min, max);
        ImGui.SameLine();
        if (ImGui.SmallButton("All in")) _raiseTo = max;
        ImGui.EndDisabled();

        var verb = table.CurrentBet == 0 ? "Bet" : "Raise to";
        var label = $"{verb} {_raiseTo}" + (_raiseTo >= max ? ", all in" : "");
        if (Ui.Key(label, 0f, KeyStyle.Plain, table.CanRaise, table.CanRaise ? "Push chips forward." : "No raise open to you here: call or fold."))
            send(new HoldemAct(HoldemMove.Raise, _raiseTo));
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
        if (seat.Folded) return "folded";
        if (seat.AllIn) return $"all in for {seat.Committed}";
        return seat.StreetBet > 0 ? $"bet {seat.StreetBet}" : "";
    }

    private static string StreetName(HoldemStreet street) => street switch
    {
        HoldemStreet.Preflop => "before the flop",
        HoldemStreet.Flop => "the flop",
        HoldemStreet.Turn => "the turn",
        HoldemStreet.River => "the river",
        _ => "showdown",
    };

    private static string CardList(IReadOnlyList<string> codes) =>
        codes.Count == 0 ? "nothing" : string.Join(", ", codes.Select(Card.NameOf));
}
