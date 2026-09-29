using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Games.ShipCaptainCrew;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

/// <summary>
/// Ship, Captain and Crew: the thrower's five dice in the middle of the felt, the ship, the
/// captain and the crew rimmed in gold as they come aboard, and every plate showing what that
/// seat has found so far and what it held. Your seat has the two keys the game has: roll, and
/// hold the cargo.
/// </summary>
public sealed class ShipCaptainCrewClient : IClientGame
{
    private const float FeltDieSize = 34f;
    private const float SeatDieSize = 30f;
    private const float PlateDieSize = 14f;
    private const string RollKey = "scc";

    public string GameType => ShipCaptainCrewModule.Type;
    public string CurrentPlayerId => Table?.CurrentPlayerId ?? "";

    public SccTable? Table { get; private set; }

    /// <summary>How the last round ended, kept on the felt until the next one is thrown.</summary>
    public SccRoundEnded? LastRound { get; private set; }

    public void Reset()
    {
        Table = null;
        LastRound = null;
    }

    public bool Apply(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case SccTurnStarted m:
            {
                var newRound = Table is null || m.Table.Round != Table.Round;
                var rollOff = m.Table.Seats.Any(s => !s.InRound);
                SetTable(m.Table, session);
                if (newRound) LastRound = null;
                session.AddLog(newRound
                    ? (rollOff ? $"Roll-off. {session.NameOf(m.PlayerId)} takes the dice." : $"Round {m.Table.Round}. {session.NameOf(m.PlayerId)} takes the dice.")
                    : $"{session.NameOf(m.PlayerId)} takes the dice.");
                return true;
            }

            case SccRolled m:
            {
                var before = Table?.Seats.FirstOrDefault(s => s.PlayerId == m.PlayerId);
                SetTable(m.Table, session);
                var seat = m.Table.Seats.First(s => s.PlayerId == m.PlayerId);
                var found = Found(before, seat);
                var name = session.NameOf(m.PlayerId);
                session.AddLog(found.Count > 0
                    ? $"{name} rolls {string.Join(" ", m.Dice)} and finds {Join(found)}."
                    : seat.Crew ? $"{name} throws the cargo again: {seat.Score}."
                    : $"{name} rolls {string.Join(" ", m.Dice)} and finds nothing.");

                Fx.Roll(RollKey, 0.7);
                Sound.Dice();
                for (var i = 0; i < found.Count; i++)
                    Fx.Float(m.PlayerId, found[i] + "!", Theme.Accent, 0.7 + i * 0.3);
                if (seat.Crew && found.Count == 0)
                    Fx.Float(m.PlayerId, $"cargo {seat.Score}", Theme.Text, 0.7);
                return true;
            }

            case SccHeld m:
            {
                SetTable(m.Table, session);
                var name = session.NameOf(m.PlayerId);
                if (m.Score == 0)
                {
                    session.AddLog($"{name} runs out of rolls with no crew.");
                    Fx.Float(m.PlayerId, "no cargo", Theme.Bad, 0.2);
                    Fx.Glow(m.PlayerId, Theme.Bad, 1.2, 0.2);
                    if (m.PlayerId == session.MyId)
                    {
                        Fx.StampFelt("No crew", Theme.Bad, 1.0, 0.2);
                        Fx.Cue(Sound.Alert, 0.2);
                    }
                }
                else
                {
                    session.AddLog($"{name} holds with a cargo of {m.Score}.");
                    Fx.Float(m.PlayerId, $"cargo {m.Score}", Theme.Accent);
                    if (m.Table.LeaderId == m.PlayerId)
                        Fx.Glow(m.PlayerId, Theme.Accent, 1.2);
                }
                return true;
            }

            case SccRoundEnded m:
            {
                SetTable(m.Table, session);
                LastRound = m;
                if (m.WinnerId is { } winner)
                {
                    session.AddLog($"{session.NameOf(winner)} takes the round with a cargo of {m.Score}.");
                    Fx.StampFelt(winner == session.MyId ? "Round yours" : $"{session.NameOf(winner)} takes it", winner == session.MyId ? Theme.Good : Theme.Accent, 1.6);
                    Fx.Float(winner, "+1 round", Theme.Good, 0.3);
                    Fx.Glow(winner, Theme.Good, 2.0);
                    if (winner == session.MyId) Sound.Chips();
                }
                else if (m.TieBreak)
                {
                    session.AddLog($"A tie at {m.Score}: {Join(m.Tied.Select(session.NameOf).ToList())} roll it off.");
                    Fx.StampFelt("Roll-off!", Theme.Accent, 1.4);
                    foreach (var id in m.Tied) Fx.Glow(id, Theme.Accent, 1.6);
                    Sound.Alert();
                }
                else
                {
                    session.AddLog("Nobody got a crew aboard. The round is thrown again.");
                    Fx.StampFelt("No cargo at all", Theme.TextDim, 1.2);
                }
                return true;
            }

            case SccSnapshot m:
                SetTable(m.Table, session);
                return true;

            default:
                return false;
        }
    }

    public string? Narrate(NetMessage message, GameSession session) => message switch
    {
        SccTurnStarted m when Table is { } t && m.Table.Round != t.Round => $"Round {m.Table.Round}. The dice go to {session.NameOf(m.PlayerId)}.",
        SccRolled m when Found(Table?.Seats.FirstOrDefault(s => s.PlayerId == m.PlayerId), m.Table.Seats.First(s => s.PlayerId == m.PlayerId)) is { Count: > 0 } found =>
            $"{session.NameOf(m.PlayerId)} shakes out {Join(found)}!",
        SccHeld m => m.Score == 0
            ? $"{session.NameOf(m.PlayerId)} throws three times and never gets a crew together."
            : $"{session.NameOf(m.PlayerId)} holds: a cargo of {m.Score}.",
        SccRoundEnded { WinnerId: { } winner } m => $"{session.NameOf(winner)} takes the round with {m.Score}.",
        SccRoundEnded { TieBreak: true } m => $"Tied at {m.Score}. The dice go round again for {Join(m.Tied.Select(session.NameOf).ToList())}.",
        SccRoundEnded => "Not a crew among them. The dice go round again.",
        _ => null,
    };

    // ------------------------------------------------------------------ the display

    public DisplayState Display(GameSession session)
    {
        if (Table is not { } table)
            return new("SETTING UP", Theme.TextDim);

        var me = table.Seats.FirstOrDefault(s => s.PlayerId == session.MyId);
        if (me is null)
            return new("WAITING", Theme.TextDim);

        if (LastRound is { } round)
        {
            if (round.WinnerId == session.MyId) return new("ROUND YOURS", Theme.Good);
            if (round.TieBreak && round.Tied.Contains(session.MyId)) return new("ROLL-OFF", Theme.Accent);
            return new("WAITING", Theme.TextDim);
        }

        if (!me.InRound)
            return new("SITTING OUT", Theme.TextDim);
        if (table.CurrentPlayerId == session.MyId)
            return new("YOUR TURN", Theme.Accent);
        return new("WAITING", Theme.TextDim);
    }

    // --------------------------------------------------------------------- the felt

    public void DrawSeatTally(PlayerPublic seat) =>
        ImGui.TextColored(Theme.TextDim, $"{seat.Tally} of {Table?.RoundsToWin ?? 3} rounds");

    public void DrawFelt(GameSession session)
    {
        if (Table is not { } table)
        {
            Theme.Marquee("Setting up", Theme.TextFaint, "Waiting for the table.");
            return;
        }

        Felt.Plates(session, DrawSeatTally, table.CurrentPlayerId, trailing: p => DrawPlateTurn(p, table));
        ImGui.Dummy(new Vector2(0f, 10f));

        if (LastRound is { } round)
        {
            DrawRoundResult(round, session);
            return;
        }

        var current = table.Seats.FirstOrDefault(s => s.PlayerId == table.CurrentPlayerId);
        if (current is null)
        {
            Theme.Marquee("Between rounds", Theme.TextFaint);
            return;
        }

        var thrown = current.RollsLeft < ShipCaptainCrewGame.RollsPerTurn;
        Theme.Marquee(thrown ? $"Roll {ShipCaptainCrewGame.RollsPerTurn - current.RollsLeft} of {ShipCaptainCrewGame.RollsPerTurn}" : "The dice",
            Theme.Accent, $"{session.NameOf(current.PlayerId)} is throwing");
        ImGui.Dummy(new Vector2(0f, 4f));

        if (thrown)
        {
            Ui.CenterNext(5f * FeltDieSize + 4f * 6f);
            DiceRenderer.Hand(current.Dice, FeltDieSize, gap: 6f, roll: RollKey, keep: current.Kept);
        }
        else
        {
            Ui.CenterNext(5f * FeltDieSize + 4f * 6f);
            DiceRenderer.HiddenHand(5, FeltDieSize, gap: 6f);
        }

        ImGui.Dummy(new Vector2(0f, 4f));
        var needs = Needs(current);
        Ui.CenterNext(ImGui.CalcTextSize(needs).X);
        ImGui.TextColored(Theme.TextDim, needs);

        var leader = table.LeaderId is { } id ? $"Best so far: {session.NameOf(id)} with a cargo of {table.LeaderScore}." : "No cargo held yet this round.";
        Ui.CenterNext(ImGui.CalcTextSize(leader).X);
        ImGui.TextColored(Theme.TextFaint, leader);
    }

    /// <summary>What a seat has found this round: the three marks, lit as they come aboard, and the cargo or the lack of it.</summary>
    private static void DrawPlateTurn(PlayerPublic p, SccTable table)
    {
        var seat = table.Seats.FirstOrDefault(s => s.PlayerId == p.Id);
        if (seat is null)
            return;

        if (!seat.InRound)
        {
            ImGui.TextColored(Theme.TextFaint, "sitting this one out");
            return;
        }

        Mark(6, seat.Ship);
        ImGui.SameLine(0f, 3f);
        Mark(5, seat.Captain);
        ImGui.SameLine(0f, 3f);
        Mark(4, seat.Crew);
        ImGui.SameLine(0f, 8f);

        var thrown = seat.RollsLeft < ShipCaptainCrewGame.RollsPerTurn;
        var text = seat.Done
            ? seat.Score > 0 ? $"cargo {seat.Score}" : "no crew"
            : thrown ? seat.Crew ? $"cargo {seat.Score}, {seat.RollsLeft} left" : $"{seat.RollsLeft} roll{(seat.RollsLeft == 1 ? "" : "s")} left"
            : "waiting";
        ImGui.TextColored(seat.Done && seat.Score == 0 ? Theme.Bad : seat.Done ? Theme.Text : Theme.TextFaint, text);
    }

    private static void Mark(int face, bool aboard)
    {
        if (!aboard) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.3f);
        DiceRenderer.Die(face, PlateDieSize, highlight: aboard);
        if (!aboard) ImGui.PopStyleVar();
    }

    private static void DrawRoundResult(SccRoundEnded round, GameSession session)
    {
        if (round.WinnerId is { } winner)
        {
            Theme.Marquee($"{session.NameOf(winner)} takes it", Theme.Accent, $"A cargo of {round.Score}. The next round is about to be thrown.");
            return;
        }

        if (round.TieBreak)
        {
            Theme.Marquee("Roll-off", Theme.Accent, $"Tied at {round.Score}: {Join(round.Tied.Select(session.NameOf).ToList())} throw again, and only they do.");
            return;
        }

        Theme.Marquee("No cargo at all", Theme.TextDim, "Nobody got a crew aboard. The round is thrown again.");
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

        var thrown = me.RollsLeft < ShipCaptainCrewGame.RollsPerTurn;
        Theme.Heading("Your dice");
        if (thrown)
        {
            var gap = MathF.Max(2f, (Ui.Avail() - 5f * SeatDieSize) / 4f);
            DiceRenderer.Hand(me.Dice, SeatDieSize, gap: gap, roll: RollKey, keep: me.Kept);
            ImGui.Dummy(new Vector2(0f, 2f));
            Ui.Hint(me.Done ? (me.Score > 0 ? $"You held with a cargo of {me.Score}." : "No crew this round.") : Needs(me));
        }
        else
        {
            Ui.Hint(me.InRound ? "Not thrown yet. You need a 6, then a 5, then a 4; the other two are your cargo." : "You're sitting this roll-off out.");
        }

        Theme.Heading("Your move");
        if (table.CurrentPlayerId != session.MyId)
        {
            Ui.Hint(!me.InRound ? "Only the tied seats throw." : table.CurrentPlayerId is { } current ? $"Waiting for {session.NameOf(current)}." : "The round is being settled.");
            return;
        }

        var toBeat = table.LeaderScore ?? 0;
        var ahead = me.Crew && me.Score > toBeat;
        if (Ui.Key(thrown ? (me.Crew ? "Throw the cargo" : "Roll again") : "Roll", 0f, me.Crew && ahead ? KeyStyle.Plain : KeyStyle.Lit, me.RollsLeft > 0,
                me.Crew ? "Throw both cargo dice again. What they show is what you get." : "Throw every die not set aside."))
            send(new SccRoll());

        if (Ui.Key(me.Crew ? $"Hold {me.Score}" : "Hold", 0f, ahead ? KeyStyle.Lit : KeyStyle.Plain, me.Crew && thrown,
                !thrown ? "Roll first." : !me.Crew ? "You need the crew aboard to hold." : ahead ? $"Keep {me.Score}. That beats the table so far." : $"Keep {me.Score}. It won't beat {toBeat} on its own."))
            send(new SccHold());
    }

    // ------------------------------------------------------------------- wording

    /// <summary>Keeps the shared roster's round counts in step with the table.</summary>
    private void SetTable(SccTable table, GameSession session)
    {
        Table = table;
        session.SetPlayers(session.Players
            .Select(p => table.Seats.FirstOrDefault(s => s.PlayerId == p.Id) is { } seat
                ? p with { Tally = seat.Points }
                : p)
            .ToArray());
    }

    /// <summary>What came aboard with the last throw, in order.</summary>
    private static List<string> Found(SccSeat? before, SccSeat after)
    {
        var found = new List<string>();
        if (after.Ship && before is not { Ship: true }) found.Add("the ship");
        if (after.Captain && before is not { Captain: true }) found.Add("the captain");
        if (after.Crew && before is not { Crew: true }) found.Add("the crew");
        return found;
    }

    private static string Needs(SccSeat seat)
    {
        if (!seat.Ship) return "Needs a 6 for the ship.";
        if (!seat.Captain) return "Ship aboard. Needs a 5 for the captain.";
        if (!seat.Crew) return "Captain aboard. Needs a 4 for the crew.";
        return seat.RollsLeft > 0
            ? $"Full complement. Cargo {seat.Score}, with {seat.RollsLeft} roll{(seat.RollsLeft == 1 ? "" : "s")} left to better it."
            : $"Full complement. Cargo {seat.Score}.";
    }

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
