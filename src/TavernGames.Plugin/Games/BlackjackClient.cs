using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Cards;
using TavernGames.Core.Games.Blackjack;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

public sealed class BlackjackClient : IClientGame
{
    private const float DealerCardWidth = 34f;
    private const float SeatCardWidth = 30f;
    private const float PlateCardWidth = 26f;

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

                // The cards go round the table, seat by seat, the dealer last.
                Sound.Deal();
                var seatIndex = 0;
                foreach (var seat in m.Table.Seats)
                    Fx.Deal($"bj.{seat.PlayerId}", delay: seatIndex++ * 0.15);
                Fx.Deal("bj.dealer", delay: seatIndex * 0.15);
                return true;

            case BjPlayed m:
                SetTable(m.Table, session);
                session.AddLog(DescribePlay(m, session));
                StagePlay(m, session);
                return true;

            case BjDealerPlayed m:
                SetTable(m.Table, session);
                session.AddLog(m.Reveal
                    ? $"The dealer turns over the {Card.NameOf(m.Card)}: {m.Table.DealerTotal}."
                    : $"The dealer draws the {Card.NameOf(m.Card)}: {m.Table.DealerTotal}{(m.Table.DealerTotal > 21 ? ", bust!" : ".")}");

                if (m.Reveal)
                    Fx.Flip("bj.dealer");
                else
                    Fx.Deal("bj.dealer", from: Math.Max(0, m.Table.DealerCards.Length - 1));
                if (m.Table.DealerTotal > 21)
                    Fx.StampFelt("Dealer busts", Theme.Good, 1.3, 0.35);
                return true;

            case BjRoundSettled m:
                LastPayouts = m.Payouts;
                SetTable(m.Table, session);
                foreach (var payout in m.Payouts)
                    session.AddLog($"{session.NameOf(payout.PlayerId)}: {OutcomeText(payout)}");
                StageSettlement(m.Payouts, session);
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

    // -------------------------------------------------------------------- the stage

    /// <summary>A hit or a double brings a card; a bust is stamped for you, floated for anyone else.</summary>
    private static void StagePlay(BjPlayed m, GameSession session)
    {
        var seat = m.Table.Seats.FirstOrDefault(s => s.PlayerId == m.PlayerId);
        if (seat is null)
            return;

        if (m.Action == "stand")
        {
            Fx.Float(m.PlayerId, $"stands on {seat.Total}", Theme.TextDim);
            return;
        }

        Fx.Deal($"bj.{m.PlayerId}", from: Math.Max(0, seat.Cards.Length - 1));
        Sound.Deal();
        if (seat.Total <= 21)
            return;

        if (m.PlayerId == session.MyId)
        {
            Fx.StampFelt("Bust!", Theme.Bad, 1.2, 0.3);
            Fx.Cue(Sound.Alert, 0.3);
        }
        else
        {
            Fx.Float(m.PlayerId, "bust", Theme.Bad, 0.3);
        }
        Fx.Glow(m.PlayerId, Theme.Bad, 1.2, 0.3);
    }

    /// <summary>Every seat's result rises off its plate; winners get their chips; yours gets the big word.</summary>
    private static void StageSettlement(BjPayout[] payouts, GameSession session)
    {
        foreach (var payout in payouts)
        {
            var colour = payout.Net > 0 ? Theme.Good : payout.Net < 0 ? Theme.Bad : Theme.TextDim;
            var text = payout.Net > 0 ? $"+{payout.Net}" : payout.Net < 0 ? payout.Net.ToString() : "push";
            Fx.Float(payout.PlayerId, text, colour);
            if (payout.Net > 0)
            {
                Fx.Fly(payout.PlayerId, payout.Net / 10 + 2, Theme.Accent);
                Fx.Glow(payout.PlayerId, Theme.Good, 1.4);
            }

            if (payout.PlayerId != session.MyId)
                continue;

            switch (payout.Outcome)
            {
                case BjOutcome.Blackjack:
                    Fx.StampFelt("Blackjack!", Theme.Good, 1.6);
                    Sound.Chips();
                    break;
                case BjOutcome.Win:
                    Fx.StampFelt("You win", Theme.Good, 1.3);
                    Sound.Chips();
                    break;
                case BjOutcome.Push:
                    Fx.StampFelt("Push", Theme.TextDim, 1.0);
                    break;
                case BjOutcome.Bust:
                    break; // already stamped when the card came
                default:
                    Fx.StampFelt("House wins", Theme.Bad, 1.3);
                    break;
            }
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
        if (me.Out)
            return new("OUT OF CHIPS", Theme.TextDim);

        if (LastPayouts.FirstOrDefault(p => p.PlayerId == session.MyId) is { } payout)
        {
            return payout.Outcome switch
            {
                BjOutcome.Blackjack => new("BLACKJACK!", Theme.Good),
                BjOutcome.Win => new("YOU WIN", Theme.Good),
                BjOutcome.Push => new("PUSH", Theme.TextDim),
                BjOutcome.Bust => new("BUST!", Theme.Bad),
                _ => new("HOUSE WINS", Theme.Bad),
            };
        }

        if (table.Betting)
            return me.Bet == 0 ? new("PLACE YOUR BET", Theme.Accent) : new("WAITING", Theme.TextDim);
        if (table.CurrentPlayerId == session.MyId)
            return new("YOUR TURN", Theme.Accent);
        if (me.State == BjHandState.Bust)
            return new("BUST!", Theme.Bad);
        if (table.CurrentPlayerId is null && table.DealerCards.Length > 0)
            return new("DEALER PLAYS", Theme.TextDim);
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

        DrawDealer(table);
        ImGui.Dummy(new Vector2(0f, 10f));
        Felt.Plates(session, DrawSeatTally, table.CurrentPlayerId, trailing: seat => DrawPlateHand(seat, table));
    }

    /// <summary>The dealer at the head of the table: the hand, and what it shows.</summary>
    private static void DrawDealer(BjTable table)
    {
        if (table.DealerCards.Length == 0)
        {
            Theme.Marquee("Place your bets", Theme.TextFaint, $"Round {table.Round} of {table.TotalRounds} · minimum bet {table.MinBet}");
            return;
        }

        var hidden = table.DealerCards.Contains(Card.HiddenCode);
        var word = hidden ? $"Dealer shows {table.DealerTotal}"
            : table.DealerTotal > 21 ? "Dealer busts"
            : $"Dealer has {table.DealerTotal}";
        Theme.Marquee(word, table.DealerTotal > 21 && !hidden ? Theme.Good : Theme.Accent, $"Round {table.Round} of {table.TotalRounds}");

        ImGui.Dummy(new Vector2(0f, 2f));
        var count = table.DealerCards.Length;
        Ui.CenterNext(count * DealerCardWidth + (count - 1) * 4f);

        // The hole card is the second one; it is the only card at the table that ever flips.
        CardRenderer.Hand(table.DealerCards, DealerCardWidth, deal: "bj.dealer", flip: "bj.dealer", flipFrom: 1);
    }

    /// <summary>A seat's cards on its plate, with the total and how the hand stands.</summary>
    private void DrawPlateHand(PlayerPublic p, BjTable table)
    {
        var seat = table.Seats.FirstOrDefault(s => s.PlayerId == p.Id);
        if (seat is null || seat.Out)
            return;

        if (seat.Cards.Length == 0)
        {
            if (table.Betting)
                ImGui.TextColored(Theme.TextFaint, seat.Bet > 0 ? $"bet {seat.Bet}" : "deciding on a bet");
            return;
        }

        var width = seat.Cards.Length > 4 ? (Felt.PlateWidth - (seat.Cards.Length - 1) * 3f) / seat.Cards.Length : PlateCardWidth;
        CardRenderer.Hand(seat.Cards, width, gap: 3f, deal: $"bj.{seat.PlayerId}");

        var total = seat.Soft && seat.Total < 21 ? $"soft {seat.Total}" : seat.Total.ToString();
        ImGui.TextColored(Theme.Text, total);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextFaint, $"bet {seat.Bet}");

        var payout = LastPayouts.FirstOrDefault(x => x.PlayerId == seat.PlayerId);
        if (payout is not null)
            ImGui.TextColored(payout.Net > 0 ? Theme.Good : payout.Net < 0 ? Theme.Bad : Theme.Accent, OutcomeText(payout));
        else if (seat.State != BjHandState.Playing)
            ImGui.TextColored(seat.State == BjHandState.Bust ? Theme.Bad : Theme.Accent, StateText(seat.State));
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

        // The hand is on the felt too, but it is yours: it belongs on your seat, where the
        // folded window still shows it.
        Theme.Heading("Your hand");
        if (me.Cards.Length == 0)
        {
            Ui.Hint(me.Out ? "No chips left." : "No cards yet.");
        }
        else
        {
            var width = me.Cards.Length > 5 ? (Ui.Avail() - (me.Cards.Length - 1) * 3f) / me.Cards.Length : SeatCardWidth;
            CardRenderer.Hand(me.Cards, width, gap: 3f, deal: $"bj.{me.PlayerId}");
            var total = me.Soft && me.Total < 21 ? $"soft {me.Total}" : me.Total.ToString();
            ImGui.TextColored(me.Total > 21 ? Theme.Bad : Theme.Text, total);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextFaint, $"· bet {me.Bet} · {me.Chips} chips");
        }

        if (me.Out)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("You're out of chips. Stay and watch the rest.");
            return;
        }

        if (table.Betting)
        {
            DrawBetting(table, me, session, send);
            return;
        }

        Theme.Heading("Your move");
        if (table.CurrentPlayerId == session.MyId)
        {
            if (Ui.Key("Hit", 0f, KeyStyle.Lit, true, "Take another card."))
                send(new BjHit());

            if (Ui.Key("Stand", 0f, KeyStyle.Plain, true, $"Stay on {me.Total}."))
                send(new BjStand());

            var canDouble = me.Cards.Length == 2 && me.Chips >= me.Bet;
            if (Ui.Key($"Double (+{me.Bet})", 0f, KeyStyle.Plain, canDouble,
                    canDouble ? "Double the bet, take exactly one more card." : me.Cards.Length != 2 ? "Only on your first two cards." : "Not enough chips to double."))
                send(new BjDouble());
        }
        else if (table.CurrentPlayerId is { } current)
        {
            Ui.Hint($"Waiting for {session.NameOf(current)}.");
        }
        else
        {
            Ui.Hint("The dealer is playing.");
        }
    }

    private void DrawBetting(BjTable table, BjSeat me, GameSession session, Action<NetMessage> send)
    {
        Theme.Heading("Your bet");

        if (me.Bet > 0)
        {
            var waiting = table.Seats.Where(s => !s.Out && s.Bet == 0).Select(s => session.NameOf(s.PlayerId)).ToList();
            Ui.Hint(waiting.Count == 0 ? $"Bet {me.Bet} placed." : $"Bet {me.Bet} placed. Waiting for {string.Join(", ", waiting)}.");
            return;
        }

        _betAmount = Math.Clamp(_betAmount == 0 ? table.MinBet : _betAmount, table.MinBet, Math.Max(table.MinBet, me.Chips));
        ImGui.SetNextItemWidth(Ui.Avail());
        ImGui.SliderInt("##bet", ref _betAmount, table.MinBet, Math.Max(table.MinBet, me.Chips));

        if (ImGui.SmallButton("Min")) _betAmount = table.MinBet;
        ImGui.SameLine();
        if (ImGui.SmallButton("Half")) _betAmount = Math.Max(table.MinBet, me.Chips / 2);
        ImGui.SameLine();
        if (ImGui.SmallButton("All in")) _betAmount = me.Chips;

        if (Ui.Key($"Bet {_betAmount}", 0f, KeyStyle.Lit, me.Chips >= table.MinBet, $"Put {_betAmount} of your {me.Chips} chips on the hand."))
            send(new BjBet(_betAmount));
    }

    // ------------------------------------------------------------------- wording

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
