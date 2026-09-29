using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// The table: a green felt inside a wooden rail, with a plate for every seat. Games draw the
/// middle of it themselves; the rail, the felt and the plates are the same for all of them so
/// every game looks like it is played in the same room.
/// </summary>
internal static class Felt
{
    private const float RailWidth = 6f;
    private const float Padding = 12f;
    private const float Rounding = 22f;

    private const float PlateMinWidth = 150f;
    private const float PlateGap = 8f;
    private const float PlatePad = 7f;
    private const float PlateMinHeight = 46f;

    private static Vector2 _start;
    private static Vector2 _size;
    private static Vector2 _inner0;
    private static Vector2 _inner1;

    /// <summary>Where each seat's plate was drawn this frame, so a cue can rise from it or fly to it.</summary>
    private static readonly Dictionary<string, (Vector2 Min, Vector2 Max)> PlateRects = new();

    /// <summary>The inner width of the plate being drawn, for tallies that want to fill it.</summary>
    public static float PlateWidth { get; private set; } = PlateMinWidth;

    /// <summary>
    /// Draws the rail and the felt over the given height and opens a child clipped to it. The
    /// child scrolls if a game draws more than fits. Always pair with <see cref="End"/>.
    /// </summary>
    public static bool Begin(string id, float height)
    {
        var dl = ImGui.GetWindowDrawList();
        _start = ImGui.GetCursorScreenPos();
        _size = new Vector2(ImGui.GetContentRegionAvail().X, MathF.Max(height, 80f));
        PlateRects.Clear();

        dl.AddRectFilled(_start, _start + _size, Theme.U32(Theme.Rail), Rounding);
        _inner0 = _start + new Vector2(RailWidth, RailWidth);
        _inner1 = _start + _size - new Vector2(RailWidth, RailWidth);
        dl.AddRectFilled(_inner0, _inner1, Theme.U32(Theme.FeltFill), Rounding - RailWidth);
        dl.AddRect(_inner0, _inner1, Theme.U32(Theme.FeltEdge), Rounding - RailWidth, ImDrawFlags.RoundCornersAll, 1f);

        ImGui.SetCursorScreenPos(_start + new Vector2(RailWidth + Padding, RailWidth + Padding));
        var childSize = _size - new Vector2((RailWidth + Padding) * 2f, (RailWidth + Padding) * 2f);
        return ImGui.BeginChild(id, childSize, false, ImGuiWindowFlags.None);
    }

    /// <summary>Closes the felt, with everything the stage is playing drawn over it first.</summary>
    public static void End()
    {
        Fx.DrawOverlay(ImGui.GetWindowDrawList(), _inner0, _inner1, Anchor);
        ImGui.EndChild();
        ImGui.SetCursorScreenPos(_start);
        ImGui.Dummy(_size);
    }

    /// <summary>Where a cue's anchor is on screen: a plate's top edge by player id, or the pot in the middle.</summary>
    private static Vector2? Anchor(string key)
    {
        if (key == "pot")
            return new Vector2((_inner0.X + _inner1.X) / 2f, _inner0.Y + MathF.Min(70f, (_inner1.Y - _inner0.Y) * 0.35f));
        return PlateRects.TryGetValue(key, out var rect)
            ? new Vector2((rect.Min.X + rect.Max.X) / 2f, rect.Min.Y)
            : null;
    }

    /// <summary>
    /// The seats round the table, as plates in a grid: the name and who it is, then whatever
    /// the game tallies for that seat, then anything else the game wants on the plate (cards,
    /// a bet). The plate of the seat on the clock is lit; a seat that is out goes faint. Empty
    /// seats, when asked for, are drawn as outlines so the lobby shows the room to fill.
    /// </summary>
    public static void Plates(
        GameSession session,
        Action<PlayerPublic>? tally,
        string? currentId = null,
        Action<PlayerPublic>? trailing = null,
        Action<PlayerPublic>? nameLine = null,
        int emptySeats = 0,
        Action? seatBot = null,
        float? width = null)
    {
        var players = session.Players;
        var total = players.Count + Math.Max(0, emptySeats);
        if (total == 0)
            return;

        // A game that puts something beside the plates (a wheel, say) gives them the width left over.
        var avail = width ?? ImGui.GetContentRegionAvail().X;
        var columns = Math.Clamp((int)((avail + PlateGap) / (PlateMinWidth + PlateGap)), 1, 3);
        columns = Math.Min(columns, total);
        var plateWidth = (avail - PlateGap * (columns - 1)) / columns;
        PlateWidth = plateWidth - PlatePad * 2f;

        var dl = ImGui.GetWindowDrawList();
        var left = ImGui.GetCursorScreenPos().X;
        var lit = new bool[columns];
        var faint = new bool[columns];
        var empty = new bool[columns];
        var ids = new string?[columns];

        // The lit plate breathes, slowly, so whose turn it is reads from across the room.
        var pulse = 0.65f + 0.35f * (0.5f + 0.5f * MathF.Sin((float)(Fx.Now * 4.0)));

        for (var row = 0; row * columns < total; row++)
        {
            var rowTop = ImGui.GetCursorScreenPos().Y;
            var rowHeight = PlateMinHeight;
            var count = Math.Min(columns, total - row * columns);

            // The plates have to be behind their content but their height isn't known until
            // the content is laid out: content first, plates on the channel beneath, then merge.
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            for (var c = 0; c < count; c++)
            {
                var i = row * columns + c;
                var origin = new Vector2(left + c * (plateWidth + PlateGap), rowTop);
                ImGui.SetCursorScreenPos(origin + new Vector2(PlatePad, PlatePad));
                ImGui.PushID(i);
                ImGui.BeginGroup();

                if (i < players.Count)
                {
                    var p = players[i];
                    lit[c] = p.Id == currentId && !p.Eliminated;
                    faint[c] = p.Eliminated;
                    empty[c] = false;
                    ids[c] = p.Id;
                    DrawPlate(session, p, lit[c], tally, trailing, nameLine);
                }
                else
                {
                    lit[c] = false;
                    faint[c] = true;
                    empty[c] = true;
                    ids[c] = null;
                    DrawEmptyPlate(seatBot);
                }

                ImGui.EndGroup();
                ImGui.PopID();
                rowHeight = MathF.Max(rowHeight, ImGui.GetItemRectSize().Y + PlatePad * 2f);
            }

            dl.ChannelsSetCurrent(0);
            for (var c = 0; c < count; c++)
            {
                var p0 = new Vector2(left + c * (plateWidth + PlateGap), rowTop);
                var p1 = p0 + new Vector2(plateWidth, rowHeight);
                if (!empty[c])
                    dl.AddRectFilled(p0, p1, Theme.U32(lit[c] ? Theme.PlateLit : Theme.PlateFill), 6f);

                var edge = lit[c] ? Theme.WithAlpha(Theme.Accent, pulse) : empty[c] ? Theme.WithAlpha(Theme.PlateEdge, 0.16f) : Theme.PlateEdge;
                dl.AddRect(p0, p1, Theme.U32(edge), 6f, ImDrawFlags.RoundCornersAll, 1f);

                if (ids[c] is { } id)
                {
                    PlateRects[id] = (p0, p1);

                    // A plate that just won or lost something lights up in that colour and fades.
                    var amount = Fx.GlowAmount(id, out var glow);
                    if (amount > 0f)
                    {
                        dl.AddRectFilled(p0, p1, Theme.U32(Theme.WithAlpha(glow, 0.18f * amount)), 6f);
                        dl.AddRect(p0 - new Vector2(2f, 2f), p1 + new Vector2(2f, 2f), Theme.U32(Theme.WithAlpha(glow, 0.9f * amount)), 8f, ImDrawFlags.RoundCornersAll, 3f);
                    }
                }
            }
            dl.ChannelsMerge();

            ImGui.SetCursorScreenPos(new Vector2(left, rowTop + rowHeight));
            ImGui.Dummy(new Vector2(avail, 0f));
            if ((row + 1) * columns < total)
                ImGui.SetCursorScreenPos(new Vector2(left, rowTop + rowHeight + PlateGap));
        }
    }

    private static void DrawPlate(GameSession session, PlayerPublic p, bool lit, Action<PlayerPublic>? tally, Action<PlayerPublic>? trailing, Action<PlayerPublic>? nameLine)
    {
        var tags = "";
        if (p.Id == session.MyId) tags += " you";
        if (p.Id == session.HostId) tags += " host";
        if (p.IsBot) tags += " bot";

        var tagsWidth = tags.Length > 0 ? ImGui.CalcTextSize(tags).X : 0f;
        var nameColour = p.Eliminated ? Theme.TextFaint : lit ? Theme.Accent : Theme.Text;
        ImGui.TextColored(nameColour, Ui.Fit(p.Name, PlateWidth - tagsWidth));
        if (tags.Length > 0)
        {
            ImGui.SameLine(0f, 0f);
            ImGui.TextColored(Theme.TextFaint, tags);
        }
        nameLine?.Invoke(p);

        if (p.Eliminated)
            ImGui.TextColored(Theme.TextFaint, "out");
        else if (tally is not null)
            tally(p);

        trailing?.Invoke(p);
    }

    private static void DrawEmptyPlate(Action? seatBot)
    {
        var label = "empty seat";
        var width = ImGui.CalcTextSize(label).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (PlateWidth - width) / 2f));
        ImGui.TextColored(Theme.TextFaint, label);

        if (seatBot is null)
            return;

        const string key = "+ Bot";
        var keyWidth = ImGui.CalcTextSize(key).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (PlateWidth - keyWidth) / 2f));
        if (ImGui.SmallButton(key))
            seatBot();
        Ui.Tip("Seat a bot here.");
    }

    /// <summary>A short label in the display face on the felt: DEALER, SHOWDOWN, the street.</summary>
    public static void Label(string text, Vector4? colour = null) =>
        Theme.Displayed(colour ?? Theme.Accent, text.ToUpperInvariant());
}
