using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Games.Roulette;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

/// <summary>
/// Roulette: the wheel on the left of the felt with the last numbers under it, the seats
/// beside it, and the layout across the bottom where the chips go. Your seat holds your
/// stack, the chip you are betting with, and the keys. The wheel turns for real when the
/// number comes in, and the ball is steered so it lands in that pocket.
/// </summary>
public sealed class RouletteClient : IClientGame
{
    private const double SpinSeconds = 2.8;
    private const float WheelSize = 150f;
    private const float CellHeight = 26f;
    private const float WheelTurns = 2f;
    private const float BallTurns = 4f;

    private static readonly Vector4 Red = Theme.Rgb(0xA8, 0x2E, 0x2E);
    private static readonly Vector4 Black = Theme.Rgb(0x1A, 0x1A, 0x20);
    private static readonly Vector4 Green = Theme.Rgb(0x1F, 0x7A, 0x4A);
    private static readonly Vector4 Ivory = Theme.Rgb(0xF5, 0xF0, 0xDE);
    private static readonly Vector4 Cloth = new(1f, 1f, 1f, 0.07f);

    /// <summary>Each seat's chips have a colour of their own, in seat order, so a crowded layout still reads.</summary>
    private static readonly Vector4[] ChipColours =
    {
        Theme.Accent, Theme.Good, Theme.Rgb(0x6B, 0xC7, 0xFF), Theme.Rgb(0xC9, 0x7B, 0xFF), Ivory, Theme.Rgb(0xE8, 0x6A, 0x4A),
    };

    private static readonly int[] Denominations = { 1, 2, 5, 10, 20 };

    /// <summary>Where each number sits round the wheel, by number.</summary>
    private static readonly int[] PocketIndex = BuildPocketIndex();

    private RoulettePayout[] _lastPayouts = [];
    private int? _spinNumber;
    private double _spinEndsAt = -1;
    private float _wheelAngle;
    private float _wheelFrom;
    private int _chip = 1;

    public string GameType => RouletteModule.Type;

    /// <summary>Nobody is on the clock at a roulette table: everyone bets at once.</summary>
    public string CurrentPlayerId => "";

    public RouletteTable? Table { get; private set; }

    public void Reset()
    {
        Table = null;
        _lastPayouts = [];
        _spinNumber = null;
        _spinEndsAt = -1;
        _wheelAngle = 0f;
        _chip = 1;
    }

    public bool Apply(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case RouletteBettingOpened m:
                _lastPayouts = [];
                SetTable(m.Table, session);
                session.AddLog($"Spin {m.Table.Round} of {m.Table.TotalRounds}. Place your bets.");
                return true;

            case RouletteBetPlaced m:
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)} puts {m.Bet.Amount} on {Spot(m.Bet)}.");
                Fx.Float(m.PlayerId, $"{m.Bet.Amount} on {ShortSpot(m.Bet)}", Theme.Text);
                return true;

            case RouletteBetsCleared m:
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)} takes their chips back.");
                return true;

            case RouletteReady m:
                SetTable(m.Table, session);
                session.AddLog($"{session.NameOf(m.PlayerId)}'s bets are down.");
                return true;

            case RouletteSpun m:
                SetTable(m.Table, session);
                session.AddLog("No more bets. The wheel spins.");
                StageSpin(m.Number);
                return true;

            case RouletteSettled m:
                _lastPayouts = m.Payouts;
                SetTable(m.Table, session);
                session.AddLog($"The ball lands on {m.Number} {ColourName(m.Number)}.");
                foreach (var payout in m.Payouts)
                {
                    var net = payout.Returned - payout.Staked;
                    session.AddLog(net > 0 ? $"{session.NameOf(payout.PlayerId)} wins {net}."
                        : net < 0 ? $"{session.NameOf(payout.PlayerId)} loses {-net}."
                        : $"{session.NameOf(payout.PlayerId)} breaks even.");
                }
                StageSettlement(m.Payouts, session);
                return true;

            case RouletteSnapshot m:
                SetTable(m.Table, session);
                return true;

            default:
                return false;
        }
    }

    public string? Narrate(NetMessage message, GameSession session) => message switch
    {
        RouletteBettingOpened m => $"Spin {m.Table.Round}. The croupier calls for bets.",
        RouletteSpun => "\"No more bets.\" The wheel turns and the ball rattles round the rim.",
        RouletteSettled m => $"The ball drops into {m.Number} {ColourName(m.Number)}." + Winners(m.Payouts, session),
        _ => null,
    };

    private static string Winners(RoulettePayout[] payouts, GameSession session)
    {
        var winners = payouts.Where(p => p.Returned > p.Staked).Select(p => $"{session.NameOf(p.PlayerId)} collects {p.Returned - p.Staked}.").ToList();
        return winners.Count == 0 ? " The house collects." : " " + string.Join(" ", winners);
    }

    // -------------------------------------------------------------------- the stage

    /// <summary>The wheel starts turning now; the number is stamped when the ball lands.</summary>
    private void StageSpin(int number)
    {
        _spinNumber = number;
        _wheelFrom = _wheelAngle;
        if (Fx.Enabled)
        {
            _spinEndsAt = Fx.Now + SpinSeconds;
            Fx.Roll("roulette.spin", SpinSeconds);
            Fx.StampFelt("No more bets", Theme.Accent, 0.8);
            Fx.StampFelt($"{number} {ColourName(number)}", StampColour(number), 1.6, SpinSeconds);
        }
        else
        {
            _spinEndsAt = Fx.Now;
        }
        Sound.Spin();
    }

    /// <summary>The stacks are paid once the ball is down, however early the server's word arrived.</summary>
    private void StageSettlement(RoulettePayout[] payouts, GameSession session)
    {
        var delay = Math.Max(0.0, _spinEndsAt - Fx.Now) + 0.3;
        foreach (var payout in payouts)
        {
            var net = payout.Returned - payout.Staked;
            var colour = net > 0 ? Theme.Good : net < 0 ? Theme.Bad : Theme.TextDim;
            Fx.Float(payout.PlayerId, net > 0 ? $"+{net}" : net < 0 ? net.ToString() : "even", colour, delay);
            if (net > 0)
            {
                Fx.Fly(payout.PlayerId, net / 20 + 2, Theme.Accent, delay);
                Fx.Glow(payout.PlayerId, Theme.Good, 1.6, delay);
            }
            else if (net < 0)
            {
                Fx.Glow(payout.PlayerId, Theme.Bad, 1.0, delay);
            }

            if (payout.PlayerId != session.MyId)
                continue;

            if (net > 0)
            {
                Fx.StampFelt("You win", Theme.Good, 1.4, delay + 0.4);
                Fx.Cue(Sound.Chips, delay + 0.1);
            }
            else if (net < 0)
            {
                Fx.StampFelt("House wins", Theme.Bad, 1.2, delay + 0.4);
            }
        }
    }

    // ------------------------------------------------------------------ the display

    public DisplayState Display(GameSession session)
    {
        if (Table is not { } table)
            return new("SETTING UP", Theme.TextDim);

        var me = table.Seats.FirstOrDefault(s => s.PlayerId == session.MyId);
        if (me is null)
            return new("WAITING", Theme.TextDim);
        if (Fx.Rolling("roulette.spin", out _))
            return new("NO MORE BETS", Theme.Accent);
        if (me.Out)
            return new("OUT OF CHIPS", Theme.TextDim);

        if (!table.Betting)
        {
            if (_lastPayouts.FirstOrDefault(p => p.PlayerId == session.MyId) is { } payout)
            {
                var net = payout.Returned - payout.Staked;
                return net > 0 ? new("YOU WIN", Theme.Good) : net < 0 ? new("HOUSE WINS", Theme.Bad) : new("EVEN", Theme.TextDim);
            }
            return new(_lastPayouts.Length > 0 || table.LastNumber is not null ? "SAT OUT" : "WAITING", Theme.TextDim);
        }

        return me.Done ? new("WAITING", Theme.TextDim) : new("PLACE YOUR BETS", Theme.Accent);
    }

    // --------------------------------------------------------------------- the felt

    public void DrawSeatTally(PlayerPublic seat) => ImGui.TextColored(Theme.TextDim, $"{seat.Tally} chips");

    public void DrawFelt(GameSession session)
    {
        if (Table is not { } table)
        {
            Theme.Marquee("Setting up", Theme.TextFaint, "Waiting for the table.");
            return;
        }

        // The wheel and the board of last numbers on the left, the seats beside them.
        ImGui.BeginGroup();
        DrawWheel();
        DrawHistory(table);
        ImGui.EndGroup();

        ImGui.SameLine(0f, 12f);
        ImGui.BeginGroup();
        Felt.Plates(session, DrawSeatTally,
            trailing: p => DrawPlateStake(p, table),
            nameLine: p => DrawPlateTags(p, table, session),
            width: ImGui.GetContentRegionAvail().X);
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0f, 8f));
        DrawLayout(table, session);
    }

    private void DrawPlateTags(PlayerPublic p, RouletteTable table, GameSession session)
    {
        var index = session.Players.FindIndex(x => x.Id == p.Id);
        ImGui.SameLine();
        Ui.Dot(ChipColours[Math.Max(0, index) % ChipColours.Length], "their chips on the layout are this colour");

        var seat = table.Seats.FirstOrDefault(s => s.PlayerId == p.Id);
        if (seat is { Done: true } && table.Betting)
        {
            ImGui.SameLine(0f, 2f);
            ImGui.TextColored(Theme.TextFaint, "done");
        }
    }

    private static void DrawPlateStake(PlayerPublic p, RouletteTable table)
    {
        var seat = table.Seats.FirstOrDefault(s => s.PlayerId == p.Id);
        if (seat is null || seat.Out)
            return;
        var staked = seat.Bets.Sum(b => b.Amount);
        ImGui.TextColored(Theme.TextFaint, staked > 0 ? $"{staked} on the table" : table.Betting ? "deciding" : "no bets");
    }

    /// <summary>
    /// The wheel: 37 pockets round a hub, the numbers on them, and the ball. While a spin
    /// plays the wheel turns one way and the ball the other, both easing to a stop, and the
    /// ball's final angle is the winning pocket's, so it lands exactly where the server said.
    /// </summary>
    private void DrawWheel()
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var centre = origin + new Vector2(WheelSize / 2f, WheelSize / 2f);
        var radius = WheelSize / 2f - 4f;
        var step = MathF.PI * 2f / RouletteGame.Pockets;

        var spinning = Fx.Rolling("roulette.spin", out var t);
        float wheel;
        float? ball = null;
        var ballRadius = 0.78f;

        if (spinning && _spinNumber is { } target)
        {
            // The wheel turns clockwise (its angle climbs); the ball runs against it, so its
            // angle falls: it starts whole turns ahead of the pocket and unwinds back to it.
            wheel = _wheelFrom + WheelTurns * MathF.PI * 2f * EaseOutQuad(t);
            var wheelEnd = _wheelFrom + WheelTurns * MathF.PI * 2f;
            ball = PocketAngle(target, wheelEnd) + BallTurns * MathF.PI * 2f * (1f - EaseOutCubic(t));
            ballRadius = 0.92f - 0.14f * SmoothStep((t - 0.65f) / 0.35f);
        }
        else
        {
            if (_spinEndsAt >= 0 && Fx.Now >= _spinEndsAt)
            {
                // The spin is over: the wheel rests where it ended, brought back within one turn.
                _wheelAngle = (_wheelFrom + WheelTurns * MathF.PI * 2f) % (MathF.PI * 2f);
                _spinEndsAt = -1;
            }
            wheel = _wheelAngle;
            if (_spinNumber is { } resting)
                ball = PocketAngle(resting, wheel);
        }

        // The rim, then a wedge per pocket, then the hub over their inner ends to make a ring.
        dl.AddCircleFilled(centre, radius + 4f, Theme.U32(Theme.Rail), 64);
        for (var i = 0; i < RouletteGame.Pockets; i++)
        {
            var number = RouletteGame.WheelOrder[i];
            var a0 = -MathF.PI / 2f + (i - 0.5f) * step + wheel;
            dl.PathLineTo(centre);
            dl.PathArcTo(centre, radius, a0, a0 + step, 3);
            dl.PathFillConvex(Theme.U32(PocketColour(number)));
        }

        for (var i = 0; i < RouletteGame.Pockets; i++)
        {
            var a = -MathF.PI / 2f + (i - 0.5f) * step + wheel;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            dl.AddLine(centre + dir * radius * 0.62f, centre + dir * radius, Theme.U32(Theme.WithAlpha(Theme.Rail, 0.9f)), 1f);
        }

        dl.AddCircleFilled(centre, radius * 0.62f, Theme.U32(Theme.Rail), 48);
        dl.AddCircle(centre, radius * 0.62f, Theme.U32(Theme.WithAlpha(Ivory, 0.25f)), 48, 1f);
        dl.AddCircle(centre, radius, Theme.U32(Theme.WithAlpha(Ivory, 0.3f)), 64, 1.2f);
        dl.AddCircle(centre, radius * 0.45f, Theme.U32(Theme.WithAlpha(Theme.Accent, 0.35f)), 40, 1f);
        dl.AddCircleFilled(centre, 4f, Theme.U32(Theme.Accent), 12);

        // The numbers, small, upright, riding round with their pockets.
        var font = ImGui.GetFont();
        const float numberSize = 9.5f;
        var scale = numberSize / ImGui.GetFontSize();
        for (var i = 0; i < RouletteGame.Pockets; i++)
        {
            var text = RouletteGame.WheelOrder[i].ToString();
            var a = -MathF.PI / 2f + i * step + wheel;
            var pos = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius * 0.81f;
            var size = ImGui.CalcTextSize(text) * scale;
            dl.AddText(font, numberSize, pos - size / 2f, Theme.U32(Ivory), text);
        }

        if (ball is { } angle)
        {
            var pos = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * ballRadius;
            dl.AddCircleFilled(pos + new Vector2(1f, 1.5f), 4.5f, Theme.U32(new Vector4(0f, 0f, 0f, 0.5f)), 14);
            dl.AddCircleFilled(pos, 4.5f, Theme.U32(Ivory), 14);
            dl.AddCircleFilled(pos - new Vector2(1.2f, 1.2f), 1.5f, Theme.U32(new Vector4(1f, 1f, 1f, 0.9f)), 8);
        }

        ImGui.Dummy(new Vector2(WheelSize, WheelSize));
    }

    /// <summary>The board over the wheel: the last numbers, newest first.</summary>
    private static void DrawHistory(RouletteTable table)
    {
        const float square = 15f;
        const float gap = 3f;
        var shown = Math.Min(table.History.Length, (int)((WheelSize + gap) / (square + gap)));

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var font = ImGui.GetFont();
        var scale = 9f / ImGui.GetFontSize();

        for (var i = 0; i < shown; i++)
        {
            var number = table.History[i];
            var p0 = origin + new Vector2(i * (square + gap), 2f);
            var p1 = p0 + new Vector2(square, square);
            dl.AddRectFilled(p0, p1, Theme.U32(PocketColour(number)), 2f);
            if (i == 0)
                dl.AddRect(p0, p1, Theme.U32(Theme.Accent), 2f, ImDrawFlags.RoundCornersAll, 1f);
            var text = number.ToString();
            var size = ImGui.CalcTextSize(text) * scale;
            dl.AddText(font, 9f, (p0 + p1) / 2f - size / 2f, Theme.U32(Ivory), text);
        }

        ImGui.Dummy(new Vector2(WheelSize, square + 4f));
        if (shown == 0)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, 2f));
            ImGui.TextColored(Theme.TextFaint, "no spins yet");
        }
    }

    /// <summary>
    /// The layout: the zero, the numbers in three rows, the column bets at their ends, the
    /// dozens under them and the even-money bets under those. Every spot is a button; a
    /// click puts the chip you are betting with on it, and everyone's chips are drawn where
    /// they lie, in their seat's colour.
    /// </summary>
    private void DrawLayout(RouletteTable table, GameSession session)
    {
        const float zeroWidth = 26f;
        const float columnWidth = 34f;
        const float gap = 2f;
        var avail = ImGui.GetContentRegionAvail().X;
        var cellWidth = Math.Clamp((avail - zeroWidth - columnWidth - gap * 14f) / 12f, 22f, 40f);
        var origin = ImGui.GetCursorScreenPos();

        var me = table.Seats.FirstOrDefault(s => s.PlayerId == session.MyId);
        var chip = Denomination(table.MinBet);
        var canBet = table.Betting && !session.IsSpectator && me is { Done: false, Out: false } && me.Chips >= chip;
        var context = new LayoutContext(table, session, me?.PlayerId, chip, canBet);

        float X(int column) => origin.X + zeroWidth + gap + column * (cellWidth + gap);
        float Y(int row) => origin.Y + row * (CellHeight + gap);

        Cell(context, new Vector2(origin.X, Y(0)), new Vector2(origin.X + zeroWidth, Y(2) + CellHeight), Green, "0", RouletteBetKind.Straight, 0);

        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 12; column++)
            {
                var number = column * 3 + (3 - row);
                var p0 = new Vector2(X(column), Y(row));
                Cell(context, p0, p0 + new Vector2(cellWidth, CellHeight), PocketColour(number), number.ToString(), RouletteBetKind.Straight, number);
            }

            var c0 = new Vector2(X(12), Y(row));
            Cell(context, c0, c0 + new Vector2(columnWidth, CellHeight), Cloth, "2:1", RouletteBetKind.Column, 3 - row);
        }

        string[] dozens = { "1st 12", "2nd 12", "3rd 12" };
        for (var d = 0; d < 3; d++)
        {
            var p0 = new Vector2(X(d * 4), Y(3));
            Cell(context, p0, p0 + new Vector2(cellWidth * 4f + gap * 3f, CellHeight), Cloth, dozens[d], RouletteBetKind.Dozen, d + 1);
        }

        (string Label, RouletteBetKind Kind, Vector4 Fill)[] evens =
        {
            ("1-18", RouletteBetKind.Low, Cloth), ("Even", RouletteBetKind.Even, Cloth), ("Red", RouletteBetKind.Red, Red),
            ("Black", RouletteBetKind.Black, Black), ("Odd", RouletteBetKind.Odd, Cloth), ("19-36", RouletteBetKind.High, Cloth),
        };
        for (var e = 0; e < evens.Length; e++)
        {
            var p0 = new Vector2(X(e * 2), Y(4));
            Cell(context, p0, p0 + new Vector2(cellWidth * 2f + gap, CellHeight), evens[e].Fill, evens[e].Label, evens[e].Kind, 0);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(zeroWidth + gap + 12f * (cellWidth + gap) + columnWidth, 5f * CellHeight + 4f * gap));
    }

    private sealed record LayoutContext(RouletteTable Table, GameSession Session, string? MyId, int Chip, bool CanBet);

    /// <summary>One spot on the layout: a button, its colour and label, the chips lying on it, and what a click would do.</summary>
    private void Cell(LayoutContext ctx, Vector2 p0, Vector2 p1, Vector4 fill, string label, RouletteBetKind kind, int pick)
    {
        var dl = ImGui.GetWindowDrawList();
        ImGui.SetCursorScreenPos(p0);
        var clicked = ImGui.InvisibleButton($"##spot{kind}{pick}", p1 - p0);
        var hovered = ImGui.IsItemHovered();

        dl.AddRectFilled(p0, p1, Theme.U32(fill), 3f);
        dl.AddRect(p0, p1, Theme.U32(hovered && ctx.CanBet ? Theme.Accent : Theme.WithAlpha(Ivory, 0.18f)), 3f, ImDrawFlags.RoundCornersAll, 1f);

        var size = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2((p0.X + p1.X - size.X) / 2f, p0.Y + 3f), Theme.U32(Theme.WithAlpha(Ivory, 0.92f)), label);

        // The chips on this spot, one per seat that has some here, along the bottom edge.
        var k = 0;
        var lines = new List<string>();
        for (var i = 0; i < ctx.Table.Seats.Length; i++)
        {
            var seat = ctx.Table.Seats[i];
            var bet = seat.Bets.FirstOrDefault(b => b.Kind == kind && b.Pick == pick);
            if (bet is null)
                continue;

            var colourIndex = ctx.Session.Players.FindIndex(x => x.Id == seat.PlayerId);
            var colour = ChipColours[Math.Max(0, colourIndex) % ChipColours.Length];
            var c = new Vector2(p0.X + 8f + k * 10f, p1.Y - 7f);
            dl.AddCircleFilled(c, 5.5f, Theme.U32(colour), 12);
            dl.AddCircle(c, 5.5f, Theme.U32(seat.PlayerId == ctx.MyId ? Ivory : Theme.WithAlpha(Theme.Shell, 0.8f)), 12, seat.PlayerId == ctx.MyId ? 1.5f : 1f);
            lines.Add($"{ctx.Session.NameOf(seat.PlayerId)}: {bet.Amount}");
            k++;
        }

        if (hovered)
        {
            var tip = string.Join("\n", lines);
            if (ctx.CanBet)
                tip = (tip.Length > 0 ? tip + "\n" : "") + $"Put {ctx.Chip} on {Spot(new RouletteBet(kind, pick, 0))}.";
            if (tip.Length > 0)
                ImGui.SetTooltip(tip);
        }

        if (clicked && ctx.CanBet)
            _pendingPlace = new RoulettePlace(kind, pick, ctx.Chip);
    }

    /// <summary>A click on the layout, waiting for the seat's draw to send it: the felt has no line to the server.</summary>
    private RoulettePlace? _pendingPlace;

    // --------------------------------------------------------------------- the seat

    public void DrawSeat(GameSession session, Action<NetMessage> send)
    {
        if (_pendingPlace is { } place)
        {
            _pendingPlace = null;
            send(place);
        }

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

        var staked = me.Bets.Sum(b => b.Amount);
        Theme.Heading("Your chips");
        ImGui.TextColored(Theme.Text, $"{me.Chips} chips");
        if (staked > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextFaint, $"· {staked} down");
        }

        if (me.Out)
        {
            Ui.Hint("You can't cover the minimum bet any more. Stay and watch the wheel.");
            return;
        }

        if (Fx.Rolling("roulette.spin", out _))
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("No more bets. The ball is rolling.");
            return;
        }

        if (!table.Betting)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("The stacks are being paid. The next spin opens in a moment.");
            return;
        }

        if (me.Done)
        {
            var waiting = table.Seats.Where(s => !s.Out && !s.Done).Select(s => session.NameOf(s.PlayerId)).ToList();
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint(waiting.Count == 0 ? "Bets are down." : $"Bets are down. Waiting for {string.Join(", ", waiting)}.");
            return;
        }

        Theme.Heading("Your bet");
        DrawChipPicker(table.MinBet, me.Chips);
        Ui.Hint($"Click a number or an outside bet on the felt to put {Denomination(table.MinBet)} on it. Click again to add more.");

        ImGui.Dummy(new Vector2(0f, 2f));
        if (Ui.Key(staked > 0 ? "Done betting" : "Sit this one out", 0f, staked > 0 ? KeyStyle.Lit : KeyStyle.Plain, true,
                staked > 0 ? "Lock your bets in. The wheel turns once everyone has." : "No bets this spin. The wheel turns once everyone is ready."))
            send(new RouletteDone());

        if (Ui.Key("Clear bets", 0f, KeyStyle.Plain, staked > 0, staked > 0 ? "Take every chip you put down back." : "Nothing on the table yet."))
            send(new RouletteClear());
    }

    /// <summary>The chip you bet with: five sizes, from the minimum up, pressed like the face picker in Liar's Dice.</summary>
    private void DrawChipPicker(int minBet, int chips)
    {
        const float size = 26f;
        var dl = ImGui.GetWindowDrawList();
        var gap = MathF.Max(2f, (Ui.Avail() - Denominations.Length * size) / (Denominations.Length - 1));
        var font = ImGui.GetFont();

        for (var i = 0; i < Denominations.Length; i++)
        {
            var amount = Denominations[i] * minBet;
            var affordable = amount <= chips;
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##chip{i}", new Vector2(size, size)) && affordable)
                _chip = i;
            Ui.Tip(affordable ? $"Bet with {amount}-chip stacks." : $"You don't have {amount} left.");

            var selected = i == _chip;
            var centre = pos + new Vector2(size / 2f, size / 2f);
            var colour = affordable ? Theme.Accent : Theme.TextDisabled;
            dl.AddCircleFilled(centre, size / 2f, Theme.U32(Theme.WithAlpha(colour, selected ? 0.55f : 0.18f)), 20);
            dl.AddCircle(centre, size / 2f, Theme.U32(Theme.WithAlpha(colour, selected ? 1f : 0.5f)), 20, selected ? 2f : 1f);
            dl.AddCircle(centre, size / 2f - 4f, Theme.U32(Theme.WithAlpha(Ivory, selected ? 0.5f : 0.2f)), 16, 1f);

            var text = amount.ToString();
            var textSize = text.Length > 2 ? 10f : ImGui.GetFontSize();
            var measured = ImGui.CalcTextSize(text) * (textSize / ImGui.GetFontSize());
            dl.AddText(font, textSize, centre - measured / 2f, Theme.U32(affordable ? Theme.Text : Theme.TextFaint), text);

            ImGui.SetCursorScreenPos(pos);
            ImGui.Dummy(new Vector2(size, size));
            if (i < Denominations.Length - 1)
                ImGui.SameLine(0f, gap);
        }

        if (_chip >= Denominations.Length || Denominations[_chip] * minBet > chips)
            _chip = Math.Max(0, Array.FindLastIndex(Denominations, d => d * minBet <= chips));
    }

    // ------------------------------------------------------------------- wording

    private int Denomination(int minBet) => Denominations[Math.Clamp(_chip, 0, Denominations.Length - 1)] * minBet;

    /// <summary>Keeps the shared roster's chip counts in step with the table.</summary>
    private void SetTable(RouletteTable table, GameSession session)
    {
        Table = table;
        session.SetPlayers(session.Players
            .Select(p => table.Seats.FirstOrDefault(s => s.PlayerId == p.Id) is { } seat
                ? p with { Tally = seat.Chips, Eliminated = seat.Out }
                : p)
            .ToArray());
    }

    private static string Spot(RouletteBet bet) => bet.Kind switch
    {
        RouletteBetKind.Straight => bet.Pick.ToString(),
        RouletteBetKind.Red => "red",
        RouletteBetKind.Black => "black",
        RouletteBetKind.Odd => "odd",
        RouletteBetKind.Even => "even",
        RouletteBetKind.Low => "1 to 18",
        RouletteBetKind.High => "19 to 36",
        RouletteBetKind.Dozen => bet.Pick switch { 1 => "the first dozen", 2 => "the second dozen", _ => "the third dozen" },
        RouletteBetKind.Column => $"column {bet.Pick}",
        _ => "the table",
    };

    private static string ShortSpot(RouletteBet bet) => bet.Kind switch
    {
        RouletteBetKind.Straight => bet.Pick.ToString(),
        RouletteBetKind.Dozen => $"{bet.Pick}{Ordinal(bet.Pick)} 12",
        RouletteBetKind.Column => $"col {bet.Pick}",
        RouletteBetKind.Low => "1-18",
        RouletteBetKind.High => "19-36",
        _ => bet.Kind.ToString().ToLowerInvariant(),
    };

    private static string Ordinal(int n) => n switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    private static string ColourName(int number) => number == 0 ? "green" : RouletteGame.IsRed(number) ? "red" : "black";

    private static Vector4 PocketColour(int number) => number == 0 ? Green : RouletteGame.IsRed(number) ? Red : Black;

    private static Vector4 StampColour(int number) => number == 0 ? Theme.Good : RouletteGame.IsRed(number) ? Theme.Bad : Theme.Text;

    /// <summary>The screen angle of a pocket's middle, for a given turn of the wheel. Pocket zero is at the top when the wheel is at rest.</summary>
    private static float PocketAngle(int number, float wheel) =>
        -MathF.PI / 2f + PocketIndex[number] * (MathF.PI * 2f / RouletteGame.Pockets) + wheel;

    private static int[] BuildPocketIndex()
    {
        var index = new int[RouletteGame.Pockets];
        for (var i = 0; i < RouletteGame.WheelOrder.Count; i++)
            index[RouletteGame.WheelOrder[i]] = i;
        return index;
    }

    private static float EaseOutQuad(float k) => 1f - (1f - k) * (1f - k);

    private static float EaseOutCubic(float k) => 1f - MathF.Pow(1f - k, 3f);

    private static float SmoothStep(float k)
    {
        k = Math.Clamp(k, 0f, 1f);
        return k * k * (3f - 2f * k);
    }
}
