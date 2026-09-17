using Dalamud.Bindings.ImGui;
using TavernGames.Core.Cards;
using TavernGames.Core.Games.Blackjack;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

public sealed class BlackjackClient : IClientGame
{
    private const float CardWidth = 34f;

    private int _betAmount;

    public string GameType => BlackjackModule.Type;
    public string CurrentPlayerId => Table?.CurrentPlayerId ?? "";

    /// <summary>The latest table the server sent. Every blackjack message carries a full one.</summary>
    public BjTable? Table { get; private set; }

    /// <summary>How the last round paid out, shown until the next deal.</summary>
    public BjPayout[] LastPayouts { get; private set; } = [];

    public void Reset()
    {
        Table = null;
        LastPayouts = [];
        _betAmount = 0;
    }

    public bool Apply(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case BjBettingOpened m:
                SetTable(m.Table, session);
                session.AddLog($"Round {m.Table.Round} of {m.Table.TotalRounds}. Place your bets.");
                return true;

            case BjBetPlaced m:
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)} bet {m.Amount}.");
                return true;

            case BjDealt m:
                LastPayouts = [];
                SetTable(m.Table, session);
                session.AddLog($"Cards dealt. The dealer shows the {Card.NameOf(m.Table.DealerCards.FirstOrDefault())}.");
                return true;

            case BjPlayed m:
                SetTable(m.Table, session);
                session.AddLog(DescribePlay(m, session));
                return true;

            case BjDealerPlayed m:
                SetTable(m.Table, session);
                session.AddLog(m.Reveal
                    ? $"The dealer turns over the {Card.NameOf(m.Card)}: {m.Table.DealerTotal}."
                    : $"The dealer draws the {Card.NameOf(m.Card)}: {m.Table.DealerTotal}{(m.Table.DealerTotal > 21 ? ", bust!" : ".")}");
                return true;

            case BjRoundSettled m:
                LastPayouts = m.Payouts;
                SetTable(m.Table, session);
                foreach (var payout in m.Payouts)
                    session.AddLog($"{session.NameOf(payout.PlayerId)}: {OutcomeText(payout)}");
                return true;

            case BjSnapshot m:
                SetTable(m.Table, session);
                return true;

            default:
                return false;
        }
    }

    public string? Narrate(NetMessage message, GameSession session) => message switch
    {
        BjBettingOpened m => $"Round {m.Table.Round}. The dealer calls for bets.",
        BjDealt m => $"The cards go round. The dealer shows the {Card.NameOf(m.Table.DealerCards.FirstOrDefault())}.",
        BjPlayed m => DescribePlay(m, session),
        BjDealerPlayed { Reveal: true } m => $"The dealer flips the {Card.NameOf(m.Card)} for {m.Table.DealerTotal}.",
        BjDealerPlayed m when m.Table.DealerTotal > 21 => $"The dealer draws the {Card.NameOf(m.Card)} and busts!",
        BjDealerPlayed m => $"The dealer draws the {Card.NameOf(m.Card)} for {m.Table.DealerTotal}.",
        BjRoundSettled m when m.Payouts.Length > 0 =>
            string.Join(" ", m.Payouts.Select(p => $"{session.NameOf(p.PlayerId)}: {OutcomeText(p)}")),
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

        ImGui.TextDisabled($"Round {table.Round} of {table.TotalRounds}, minimum bet {table.MinBet}");

        // The dealer.
        ImGui.TextColored(TableUi.Gold, "Dealer");
        ImGui.SameLine();
        if (table.DealerCards.Length == 0)
        {
            ImGui.TextDisabled("waiting for bets");
        }
        else
        {
            var hidden = table.DealerCards.Contains(Card.HiddenCode);
            ImGui.TextDisabled(hidden ? $"showing {table.DealerTotal}" : table.DealerTotal > 21 ? $"{table.DealerTotal}, bust" : table.DealerTotal.ToString());
            CardRenderer.Hand(table.DealerCards, CardWidth);
        }

        ImGui.Separator();

        // Every seat: name, stack, bet, cards, and how the hand stands.
        foreach (var seat in table.Seats)
            DrawSeat(seat, table, session);

        ImGui.Separator();
        DrawControls(table, session, send);
    }

    private void DrawSeat(BjSeat seat, BjTable table, GameSession session)
    {
        var isCurrent = seat.PlayerId == table.CurrentPlayerId;
        var isMe = seat.PlayerId == session.MyId;
        var isBot = session.Players.FirstOrDefault(p => p.Id == seat.PlayerId)?.IsBot == true;
        var tag = isMe ? " (you)" : isBot ? " (bot)" : "";

        ImGui.TextColored(isCurrent ? TableUi.TurnGreen : TableUi.Grey, isCurrent ? ">>" : "  ");
        ImGui.SameLine(0, 4);
        ImGui.TextColored(seat.Out ? TableUi.Grey : isCurrent ? TableUi.TurnGreen : System.Numerics.Vector4.One, session.NameOf(seat.PlayerId) + tag);
        ImGui.SameLine();
        ImGui.TextDisabled(seat.Out ? "out of chips" : $"{seat.Chips} chips" + (seat.Bet > 0 ? $", bet {seat.Bet}" : ""));

        if (seat.Cards.Length == 0)
        {
            if (table.Betting && !seat.Out && seat.Bet == 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(deciding on a bet)");
            }
            return;
        }

        CardRenderer.Hand(seat.Cards, CardWidth);
        ImGui.SameLine(0, 10);
        ImGui.BeginGroup();
        ImGui.TextUnformatted(seat.Soft && seat.Total < 21 ? $"soft {seat.Total}" : seat.Total.ToString());

        var payout = LastPayouts.FirstOrDefault(p => p.PlayerId == seat.PlayerId);
        if (payout is not null)
            ImGui.TextColored(payout.Net > 0 ? TableUi.TurnGreen : payout.Net < 0 ? TableUi.Red : TableUi.Gold, OutcomeText(payout));
        else if (seat.State != BjHandState.Playing)
            ImGui.TextColored(seat.State == BjHandState.Bust ? TableUi.Red : TableUi.Gold, StateText(seat.State));
        ImGui.EndGroup();
    }

    private void DrawControls(BjTable table, GameSession session, Action<NetMessage> send)
    {
        var me = table.Seats.FirstOrDefault(s => s.PlayerId == session.MyId);
        if (session.IsSpectator || me is null)
            return;

        if (me.Out)
        {
            ImGui.TextDisabled("You're out of chips. You can stay and watch the rest.");
            return;
        }

        if (table.Betting)
        {
            if (me.Bet > 0)
            {
                var waiting = table.Seats.Where(s => !s.Out && s.Bet == 0).Select(s => session.NameOf(s.PlayerId));
                ImGui.TextDisabled($"Bet placed. Waiting for {string.Join(", ", waiting)}...");
                return;
            }

            ImGui.TextColored(TableUi.TurnGreen, "Place your bet");
            _betAmount = Math.Clamp(_betAmount == 0 ? table.MinBet : _betAmount, table.MinBet, me.Chips);
            ImGui.SetNextItemWidth(220);
            ImGui.SliderInt("##bet", ref _betAmount, table.MinBet, me.Chips);
            ImGui.SameLine();
            if (ImGui.Button($"Bet {_betAmount}##placebet"))
                send(new BjBet(_betAmount));
            ImGui.SameLine();
            if (ImGui.SmallButton("Min")) _betAmount = table.MinBet;
            ImGui.SameLine();
            if (ImGui.SmallButton("All in")) _betAmount = me.Chips;
            return;
        }

        if (table.CurrentPlayerId == session.MyId)
        {
            ImGui.TextColored(TableUi.TurnGreen, "Your turn!");
            if (ImGui.Button("Hit")) send(new BjHit());
            ImGui.SameLine();
            if (ImGui.Button("Stand")) send(new BjStand());
            ImGui.SameLine();
            var canDouble = me.Cards.Length == 2 && me.Chips >= me.Bet;
            ImGui.BeginDisabled(!canDouble);
            if (ImGui.Button($"Double down (+{me.Bet})##double")) send(new BjDouble());
            ImGui.EndDisabled();
        }
        else if (table.CurrentPlayerId is { } current)
        {
            ImGui.TextDisabled($"Waiting for {session.NameOf(current)}...");
        }
        else
        {
            ImGui.TextDisabled("The dealer is playing...");
        }
    }

    /// <summary>Keeps the shared roster's chip counts in step with the table.</summary>
    private void SetTable(BjTable table, GameSession session)
    {
        Table = table;
        session.SetPlayers(session.Players
            .Select(p => table.Seats.FirstOrDefault(s => s.PlayerId == p.Id) is { } seat
                ? p with { Tally = seat.Chips, Eliminated = seat.Out }
                : p)
            .ToArray());
    }

    private static string DescribePlay(BjPlayed m, GameSession session)
    {
        var name = session.NameOf(m.PlayerId);
        var seat = m.Table.Seats.FirstOrDefault(s => s.PlayerId == m.PlayerId);
        var total = seat?.Total ?? 0;
        var ending = total > 21 ? $"{total}, bust!" : $"{total}.";

        return m.Action switch
        {
            "hit" => $"{name} hits and gets the {Card.NameOf(m.Card)}: {ending}",
            "double" => $"{name} doubles down and gets the {Card.NameOf(m.Card)}: {ending}",
            _ => $"{name} stands on {total}.",
        };
    }

    private static string OutcomeText(BjPayout payout) => payout.Outcome switch
    {
        BjOutcome.Blackjack => $"blackjack! +{payout.Net}",
        BjOutcome.Win => $"wins +{payout.Net}",
        BjOutcome.Push => "push, bet returned",
        BjOutcome.Bust => $"bust, {payout.Net}",
        _ => $"loses {payout.Net}",
    };

    private static string StateText(BjHandState state) => state switch
    {
        BjHandState.Blackjack => "blackjack!",
        BjHandState.Bust => "bust",
        BjHandState.Doubled => "doubled",
        BjHandState.Stood => "stands",
        _ => "",
    };
}
