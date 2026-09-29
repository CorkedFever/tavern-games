using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace TavernGames.Plugin.Windows;

/// <summary>How a key on the seat is lit.</summary>
internal enum KeyStyle
{
    /// <summary>Raised shell: an ordinary action.</summary>
    Plain,

    /// <summary>Amber-rimmed: the action the table is waiting on.</summary>
    Lit,

    /// <summary>Red-rimmed: the accusation. Calling liar, folding a hand.</summary>
    Bad,

    /// <summary>Greyed lettering: leaving, disconnecting. There, but not calling attention.</summary>
    Faint,
}

/// <summary>
/// The shared widgets. Every spacing and colour decision that more than one panel makes lives
/// here rather than at each call site, so the window stays one thing as panels are added.
/// </summary>
internal static class Ui
{
    /// <summary>The height of a key on the seat: room for the display face and a little air.</summary>
    public const float KeyHeight = 34f;

    /// <summary>
    /// While the seat is being drawn, everything is sized to its column rather than to the
    /// window: a folded window sizes itself to its content, and content sized to the window
    /// would chase it forever. Set by the seat; null everywhere else.
    /// </summary>
    internal static float? ColumnWidth;

    internal static float ColumnLeft;

    /// <summary>The width left on the current line: the column's on the seat, the window's elsewhere.</summary>
    public static float Avail() => ColumnWidth is { } width
        ? MathF.Max(0f, width - (ImGui.GetCursorPosX() - ColumnLeft))
        : ImGui.GetContentRegionAvail().X;

    /// <summary>
    /// A key: a button lettered in the display face. A width of zero fills the line. Disabled
    /// keys keep their tooltip, because "why can't I press this" is the question they answer.
    /// </summary>
    public static bool Key(string label, float width = 0f, KeyStyle style = KeyStyle.Plain, bool enabled = true, string? tip = null)
    {
        var size = new Vector2(width <= 0f ? Avail() : width, KeyHeight);
        var (fill, hovered, border, text) = style switch
        {
            KeyStyle.Lit => (Theme.GlassLit, Theme.Rgb(0x33, 0x26, 0x12), Theme.Accent, Theme.Accent),
            KeyStyle.Bad => (Theme.Rgb(0x2A, 0x11, 0x12), Theme.Rgb(0x3E, 0x18, 0x19), Theme.Bad, Theme.Bad),
            KeyStyle.Faint => (Theme.TitleBarFill, Theme.GlassLit, new Vector4(0f, 0f, 0f, 0f), Theme.TextFaint),
            _ => (Theme.TitleBarFill, Theme.GlassLit, new Vector4(0f, 0f, 0f, 0f), Theme.Text),
        };

        ImGui.PushStyleColor(ImGuiCol.Button, fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, hovered);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, border.W > 0f ? 1f : 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);

        ImGui.BeginDisabled(!enabled);
        bool pressed;
        using (Theme.PushDisplay())
            pressed = ImGui.Button(label.ToUpperInvariant(), size);
        ImGui.EndDisabled();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);

        if (tip is not null)
            Tip(tip);

        return pressed && enabled;
    }

    /// <summary>Two keys side by side on one line, each half the width.</summary>
    public static float HalfKeyWidth() => (Avail() - ImGui.GetStyle().ItemSpacing.X) / 2f;

    /// <summary>
    /// A number with a minus and a plus key either side of it, the value lettered in the display
    /// face between them. For counts small enough that a slider would be fiddly: dice, bots.
    /// </summary>
    public static bool Stepper(string id, ref int value, int min, int max, string caption)
    {
        var changed = false;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var middle = Avail() - KeyHeight * 2f - spacing * 2f;

        ImGui.PushID(id);
        if (Key("-", KeyHeight, KeyStyle.Plain, value > min))
        {
            value--;
            changed = true;
        }

        ImGui.SameLine();
        using (Theme.PushDisplay())
        {
            var text = $"{value} {caption}".ToUpperInvariant();
            var width = ImGui.CalcTextSize(text).X;
            var start = ImGui.GetCursorPosX();
            ImGui.SetCursorPosX(start + MathF.Max(0f, (middle - width) / 2f));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (KeyHeight - ImGui.GetTextLineHeight()) / 2f);
            ImGui.TextColored(Theme.Text, text);
            ImGui.SameLine();
            ImGui.SetCursorPosX(start + middle + spacing);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (KeyHeight - ImGui.GetTextLineHeight()) / 2f);
        }

        if (Key("+", KeyHeight, KeyStyle.Plain, value < max))
        {
            value++;
            changed = true;
        }
        ImGui.PopID();

        value = Math.Clamp(value, min, max);
        return changed;
    }

    /// <summary>
    /// A way to pick one of a few things: a row of chips while there are few enough to read at
    /// a glance, a dropdown once there are more. Returns the index pressed this frame, or -1.
    /// </summary>
    public static int Chips(string id, IReadOnlyList<string> labels, int selected)
    {
        var pressed = -1;
        if (labels.Count > 6)
        {
            ImGui.SetNextItemWidth(Math.Min(240f, ImGui.GetContentRegionAvail().X));
            var preview = selected >= 0 && selected < labels.Count ? labels[selected] : "Choose...";
            if (ImGui.BeginCombo($"##{id}pick", preview))
            {
                for (var i = 0; i < labels.Count; i++)
                    if (ImGui.Selectable($"{labels[i]}##{id}{i}", i == selected))
                        pressed = i;
                ImGui.EndCombo();
            }
            return pressed;
        }

        var rightEdge = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        for (var i = 0; i < labels.Count; i++)
        {
            var width = ImGui.CalcTextSize(labels[i]).X + ImGui.GetStyle().FramePadding.X * 2;

            // Wrapping needs the last item's right edge, not the cursor: after a button the
            // cursor has already moved to the next line, so it can't answer "does the next fit".
            if (i > 0 && ImGui.GetItemRectMax().X + spacing + width < rightEdge)
                ImGui.SameLine();

            var lit = i == selected;
            ImGui.PushStyleColor(ImGuiCol.Button, lit ? Theme.GlassLit : Theme.Glass);
            ImGui.PushStyleColor(ImGuiCol.Border, lit ? Theme.Accent : Theme.GlassEdge);
            ImGui.PushStyleColor(ImGuiCol.Text, lit ? Theme.Accent : Theme.Text);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            if (ImGui.SmallButton($"{labels[i]}##{id}{i}"))
                pressed = i;
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);
        }

        return pressed;
    }

    /// <summary>Grey explanatory text, wrapped to the panel, or to the column on the seat.</summary>
    public static void Hint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextFaint);
        if (ColumnWidth is { } width)
        {
            // TextWrapped wraps at the window's edge whatever wrap position is pushed; the
            // seat needs its own edge, or a folded window would grow to fit the longest line.
            ImGui.PushTextWrapPos(ColumnLeft + width);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.TextWrapped(text);
        }
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// Attaches a tooltip to whatever was drawn just before it. AllowWhenDisabled is the point:
    /// without it a greyed-out control never reports itself hovered, so exactly the tooltips that
    /// explain why something is unavailable are the ones that never appear.
    /// </summary>
    public static void Tip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>A small button showing an icon, with the tooltip an icon alone never provides.</summary>
    public static bool IconButton(FontAwesomeIcon icon, string tooltip, string id, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        bool pressed;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            pressed = ImGui.SmallButton(icon.ToIconString() + "##" + id);
        ImGui.EndDisabled();
        Tip(tooltip);
        return pressed && enabled;
    }

    /// <summary>
    /// A filled circle in the current line: an LED. Drawn rather than written because a
    /// coloured word costs a whole line and reads as an error.
    /// </summary>
    public static void Dot(Vector4 colour, string? tooltip = null)
    {
        var radius = ImGui.GetFontSize() * 0.22f;
        var size = new Vector2(radius * 2.6f, ImGui.GetTextLineHeight());
        var origin = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(origin + size * 0.5f, radius, Theme.U32(colour), 12);
        ImGui.Dummy(size);
        if (tooltip is not null)
            Tip(tooltip);
    }

    /// <summary>Sets the cursor so that an item of the given width sits centred in the line.</summary>
    public static void CenterNext(float itemWidth)
    {
        var slack = Avail() - itemWidth;
        if (slack > 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + slack / 2f);
    }

    /// <summary>
    /// Shortens a server address to something that fits on one line and still identifies it.
    /// Falls back to the raw text: a truncated address is more useful than "(unknown)".
    /// </summary>
    public static string Pretty(string source)
    {
        if (source.Length == 0)
            return "no server set";
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return Ellipsis(source, 28);
    }

    /// <summary>Right-aligns the next item on the current line; counts read better pinned to the edge.</summary>
    public static void RightAlign(float width)
    {
        var x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (x > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(x);
    }

    public static void RightAlignedText(string text, Vector4 colour)
    {
        RightAlign(ImGui.CalcTextSize(text).X);
        ImGui.TextColored(colour, text);
    }

    /// <summary>The text cut with an ellipsis to fit a width in the current font, so a line never wraps or runs off.</summary>
    public static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        var keep = text.Length;
        while (keep > 1 && ImGui.CalcTextSize(string.Concat(text.AsSpan(0, keep), "…")).X > width)
            keep -= Math.Max(1, keep / 12);
        return string.Concat(text.AsSpan(0, Math.Max(1, keep)), "…");
    }

    public static string Ellipsis(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, Math.Max(1, max - 1)), "…");
}
