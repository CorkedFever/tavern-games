using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// The look: a near-black console shell, warm dark-glass panels, a green felt for the table,
/// and one lantern-amber accent that everything the eye should land on glows in.
/// <para>
/// Built to read as a sibling of Aetherstream on the same screen: the same shell greys, the
/// same hand-drawn frame, the same rule that the display face is for short labels only and
/// never for a path or an error. Only the glass tint and the accent are ours: an aetheryte is
/// cyan, a tavern is lit by lanterns.
/// </para>
/// <para>
/// Drawn with the draw list where a hairline would vanish (the frame, the panels, the felt) and
/// pushed as style colours everywhere else, so the stock widgets inside match without every
/// call site knowing about it.
/// </para>
/// </summary>
internal static class Theme
{
    // -- palette -----------------------------------------------------------------------------

    /// <summary>The console shell the whole window is made of.</summary>
    public static readonly Vector4 Shell = Rgb(0x0B, 0x0B, 0x10);

    public static readonly Vector4 TitleBarFill = Rgb(0x15, 0x15, 0x1D);

    /// <summary>Seams and dividers in the shell.</summary>
    public static readonly Vector4 Edge = Rgb(0x2A, 0x2A, 0x34);

    /// <summary>The light catch on the shell's inner edge.</summary>
    public static readonly Vector4 FrameInner = Rgb(0x4A, 0x4A, 0x60);

    /// <summary>Panel fill: dark glass with a little warmth in it.</summary>
    public static readonly Vector4 Glass = Rgb(0x10, 0x0E, 0x0A);

    /// <summary>Panel fill for the active or selected thing.</summary>
    public static readonly Vector4 GlassLit = Rgb(0x23, 0x1A, 0x0C);

    /// <summary>The hairline round a panel, and the frame of every input.</summary>
    public static readonly Vector4 GlassEdge = Rgb(0x2C, 0x26, 0x18);

    /// <summary>The seat's display strip: the dark of a lit sign with nothing on it.</summary>
    public static readonly Vector4 StripFill = Rgb(0x1A, 0x14, 0x0A);

    /// <summary>The table's surface. Green, because every card table ever was.</summary>
    public static readonly Vector4 FeltFill = Rgb(0x0E, 0x21, 0x18);

    public static readonly Vector4 FeltEdge = Rgb(0x1A, 0x3A, 0x2A);

    /// <summary>The wooden rail round the felt.</summary>
    public static readonly Vector4 Rail = Rgb(0x1C, 0x14, 0x0B);

    /// <summary>Lantern amber. Anything the eye should land on first.</summary>
    public static readonly Vector4 Accent = Rgb(0xF0, 0xB6, 0x50);

    public static readonly Vector4 AccentDim = Accent with { W = 0.35f };

    public static readonly Vector4 Text = Rgb(0xF3, 0xEC, 0xDF);

    public static readonly Vector4 TextDim = Rgb(0xB8, 0xAC, 0x98);

    public static readonly Vector4 TextFaint = Rgb(0x8A, 0x80, 0x70);

    public static readonly Vector4 TextDisabled = Rgb(0x4A, 0x44, 0x38);

    public static readonly Vector4 Good = Rgb(0x5D, 0xCA, 0xA5);

    public static readonly Vector4 Bad = Rgb(0xE2, 0x4B, 0x4A);

    /// <summary>A seat's plate on the felt, and its edge.</summary>
    public static readonly Vector4 PlateFill = new(0f, 0f, 0f, 0.35f);

    public static readonly Vector4 PlateEdge = new(1f, 1f, 1f, 0.08f);

    /// <summary>The plate of whoever is on the clock.</summary>
    public static readonly Vector4 PlateLit = Accent with { W = 0.10f };

    public const float ShellRounding = 8f;

    public const float PanelRounding = 5f;

    // -- shell -------------------------------------------------------------------------------

    private static int _pushedColours;
    private static int _pushedVars;

    /// <summary>
    /// Restyles the host window and every stock widget drawn inside it. Pushed in PreDraw and
    /// popped in PostDraw, so nothing inside has to think about it.
    /// </summary>
    public static void PushShell()
    {
        Colour(ImGuiCol.WindowBg, Shell);
        Colour(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));
        Colour(ImGuiCol.PopupBg, Rgb(0x14, 0x12, 0x10));
        Colour(ImGuiCol.Border, GlassEdge);
        Colour(ImGuiCol.Text, Text);
        Colour(ImGuiCol.TextDisabled, TextFaint);

        // Inputs and combos are recessed glass; buttons are raised shell.
        Colour(ImGuiCol.FrameBg, Glass);
        Colour(ImGuiCol.FrameBgHovered, GlassLit);
        Colour(ImGuiCol.FrameBgActive, GlassLit);
        Colour(ImGuiCol.Button, TitleBarFill);
        Colour(ImGuiCol.ButtonHovered, GlassLit);
        Colour(ImGuiCol.ButtonActive, Rgb(0x33, 0x26, 0x12));
        Colour(ImGuiCol.CheckMark, Accent);
        Colour(ImGuiCol.SliderGrab, Accent);
        Colour(ImGuiCol.SliderGrabActive, Accent);
        Colour(ImGuiCol.Header, GlassLit);
        Colour(ImGuiCol.HeaderHovered, GlassLit);
        Colour(ImGuiCol.HeaderActive, Rgb(0x33, 0x26, 0x12));
        Colour(ImGuiCol.Separator, Edge);
        Colour(ImGuiCol.ScrollbarBg, new Vector4(0f, 0f, 0f, 0f));
        Colour(ImGuiCol.ScrollbarGrab, Edge);
        Colour(ImGuiCol.ScrollbarGrabHovered, FrameInner);
        Colour(ImGuiCol.ScrollbarGrabActive, FrameInner);
        Colour(ImGuiCol.ResizeGrip, new Vector4(0f, 0f, 0f, 0f));
        Colour(ImGuiCol.ResizeGripHovered, AccentDim);
        Colour(ImGuiCol.ResizeGripActive, Accent);
        Colour(ImGuiCol.TableHeaderBg, TitleBarFill);
        Colour(ImGuiCol.TableRowBg, new Vector4(0f, 0f, 0f, 0f));
        Colour(ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.03f));
        Colour(ImGuiCol.TableBorderLight, GlassEdge);
        Colour(ImGuiCol.TableBorderStrong, GlassEdge);
        Colour(ImGuiCol.PlotHistogram, Accent);

        Var(ImGuiStyleVar.WindowRounding, ShellRounding);

        // Our own frame replaces it; both at once is a muddy double edge.
        Var(ImGuiStyleVar.WindowBorderSize, 0f);
        Var(ImGuiStyleVar.FrameBorderSize, 1f);
        Var(ImGuiStyleVar.FrameRounding, 3f);
        Var(ImGuiStyleVar.ChildRounding, PanelRounding);
        Var(ImGuiStyleVar.PopupRounding, 4f);
        Var(ImGuiStyleVar.GrabRounding, 2f);
        Var(ImGuiStyleVar.WindowPadding, new Vector2(12f, 12f));
    }

    public static void PopShell()
    {
        if (_pushedColours > 0)
            ImGui.PopStyleColor(_pushedColours);
        if (_pushedVars > 0)
            ImGui.PopStyleVar(_pushedVars);
        _pushedColours = 0;
        _pushedVars = 0;
    }

    /// <summary>
    /// The window's own edge: a black outer line and a lighter catch inside it. Clipping is
    /// lifted because a window's draw list is clipped to its content region and the frame would
    /// otherwise be cut off at the padding.
    /// </summary>
    public static void WindowFrame()
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();

        dl.PushClipRectFullScreen();
        dl.AddRect(min, max, U32(new Vector4(0f, 0f, 0f, 1f)), ShellRounding, ImDrawFlags.RoundCornersAll, 1f);
        dl.AddRect(min + new Vector2(1f, 1f), max - new Vector2(1f, 1f), U32(FrameInner), ShellRounding - 1f, ImDrawFlags.RoundCornersAll, 1f);
        dl.PopClipRect();
    }

    // -- panels ------------------------------------------------------------------------------

    /// <summary>
    /// A glass panel sized to whatever <paramref name="content"/> draws. The frame has to be
    /// behind the content but its height isn't known until the content has been laid out, so
    /// the draw list is split in two: the content goes down first, the frame is added to the
    /// channel underneath, and the two are merged.
    /// </summary>
    public static void Panel(string id, Action content, float padding = 10f, bool lit = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + new Vector2(padding, padding));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - padding * 2);
        ImGui.PushID(id);
        content();
        ImGui.PopID();
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var height = ImGui.GetItemRectSize().Y + padding * 2;
        var end = start + new Vector2(width, height);

        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(start, end, U32(lit ? GlassLit : Glass), PanelRounding);
        dl.AddRect(start, end, U32(lit ? Accent : GlassEdge), PanelRounding, ImDrawFlags.RoundCornersAll, 1f);
        dl.ChannelsMerge();

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
    }

    // -- type --------------------------------------------------------------------------------

    /// <summary>The display face, once the plugin has loaded it. Null falls back to the default font.</summary>
    public static DisplayFont? DisplayFace { get; set; }

    public static IDisposable PushDisplay() => DisplayFace?.Push() ?? Nothing.Instance;

    public static IDisposable PushDisplayLarge() => DisplayFace?.PushLarge() ?? Nothing.Instance;

    /// <summary>
    /// A section heading: the display face in amber, upper-cased, with a hairline under it.
    /// Upper case because VT323 was drawn for a terminal that had nothing else, and its
    /// capitals are where the character lives.
    /// </summary>
    public static void Heading(string text)
    {
        ImGui.Spacing();
        using (PushDisplay())
            ImGui.TextColored(Accent, text.ToUpperInvariant());

        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var y = ImGui.GetItemRectMax().Y + 2f;
        var right = min.X + Ui.Avail();
        dl.AddLine(new Vector2(min.X, y), new Vector2(right, y), U32(GlassEdge), 1f);
        ImGui.Dummy(new Vector2(0f, 4f));
    }

    /// <summary>A short value in the display face: a number, a code, a state. Never a sentence.</summary>
    public static void Displayed(Vector4 colour, string text)
    {
        using (PushDisplay())
            ImGui.TextColored(colour, text);
    }

    /// <summary>
    /// The big words in the middle of the felt, and a quieter line under them: the standing
    /// bid, the pot, the room code, who won. The table's own way of saying what matters now.
    /// </summary>
    public static void Marquee(string word, Vector4 colour, string detail = "")
    {
        using (PushDisplayLarge())
        {
            var text = word.ToUpperInvariant();
            Ui.CenterNext(ImGui.CalcTextSize(text).X);
            ImGui.TextColored(colour, text);
        }

        if (detail.Length > 0)
        {
            Ui.CenterNext(ImGui.CalcTextSize(detail).X);
            ImGui.TextColored(TextDim, detail);
        }
    }

    /// <summary>Opaque colour at a different alpha, for things meant to sit quietly on the glass.</summary>
    public static Vector4 WithAlpha(Vector4 colour, float alpha) => colour with { W = alpha };

    public static uint U32(Vector4 colour) => ImGui.ColorConvertFloat4ToU32(colour);

    internal static Vector4 Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);

    private static void Colour(ImGuiCol target, Vector4 value)
    {
        ImGui.PushStyleColor(target, value);
        _pushedColours++;
    }

    private static void Var(ImGuiStyleVar target, float value)
    {
        ImGui.PushStyleVar(target, value);
        _pushedVars++;
    }

    private static void Var(ImGuiStyleVar target, Vector2 value)
    {
        ImGui.PushStyleVar(target, value);
        _pushedVars++;
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
