using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// Draws dice as actual faces with pips via ImGui's draw list, so the UI shows real dice
/// instead of "[2]". Hidden dice (other players' hands) are drawn face-down so you can see
/// how many each player holds without their values. Given a roll key that <see cref="Fx"/>
/// has started, a hand tumbles, bouncing and changing face, before settling one die at a time.
/// </summary>
internal static class DiceRenderer
{
    private static readonly Vector4 Face = new(0.96f, 0.94f, 0.87f, 1f);   // ivory
    private static readonly Vector4 Pip = new(0.13f, 0.11f, 0.10f, 1f);    // near-black
    private static readonly Vector4 Border = new(0.32f, 0.28f, 0.24f, 1f);
    private static readonly Vector4 HiddenFace = new(0.42f, 0.15f, 0.17f, 1f);   // muted crimson
    private static readonly Vector4 HiddenBorder = new(0.66f, 0.32f, 0.32f, 1f);
    private static readonly Vector4 HiddenMark = new(1f, 1f, 1f, 0.22f);
    private static readonly Vector4 DeadFace = new(0.28f, 0.28f, 0.28f, 1f);
    private static readonly Vector4 DeadBorder = new(0.40f, 0.40f, 0.40f, 1f);

    private static readonly Vector4 HighlightBorder = new(1f, 0.78f, 0.2f, 1f);

    /// <summary>
    /// Draws a single face-up die showing <paramref name="value"/> and advances the layout.
    /// A highlighted die gets a gold rim (used to pick out the dice that matched a bid); a
    /// <paramref name="rim"/> colour overrides it. <paramref name="lift"/> raises the drawing
    /// without moving the layout, for a bounce.
    /// </summary>
    public static void Die(int value, float size, bool highlight = false, float lift = 0f, Vector4? rim = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var q = p - new Vector2(0f, lift);
        var border = rim ?? (highlight ? HighlightBorder : Border);
        DrawBody(dl, q, size, Face, border, highlight || rim.HasValue ? 2.2f : 1f);
        DrawPips(dl, q, size, value);
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>Draws a face-down die (count is known, value hidden) and advances the layout.</summary>
    public static void HiddenDie(float size, bool dead = false, float lift = 0f)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var q = p - new Vector2(0f, lift);
        DrawBody(dl, q, size, dead ? DeadFace : HiddenFace, dead ? DeadBorder : HiddenBorder);
        if (!dead)
        {
            // A faint diamond mark hints at a concealed die.
            var c = q + new Vector2(size * 0.5f, size * 0.5f);
            var d = size * 0.17f;
            var col = ImGui.GetColorU32(HiddenMark);
            dl.AddQuadFilled(c + new Vector2(0, -d), c + new Vector2(d, 0), c + new Vector2(0, d), c + new Vector2(-d, 0), col);
        }
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>
    /// Draws a row of face-up dice, optionally rimming every die showing <paramref name="highlightFace"/>.
    /// While a roll under <paramref name="roll"/> is playing the dice tumble and settle in turn.
    /// Dice marked in <paramref name="keep"/> were set aside: they never tumble, and they are rimmed.
    /// </summary>
    public static void Hand(int[] dice, float size, float gap = 5f, int highlightFace = 0, string? roll = null, Vector4? rim = null, bool[]? keep = null)
    {
        var t = 1f;
        var rolling = roll is not null && Fx.Rolling(roll, out t);

        for (var i = 0; i < dice.Length; i++)
        {
            var kept = keep is not null && i < keep.Length && keep[i];
            var settled = kept || !rolling || t >= SettleAt(i, dice.Length);
            var face = settled ? dice[i] : Tumble(i);
            var lift = settled ? 0f : Bounce(i, t, size);
            Die(face, size, highlight: kept || (settled && dice[i] == highlightFace), lift: lift, rim: settled ? rim : null);
            if (i < dice.Length - 1) ImGui.SameLine(0, gap);
        }
    }

    /// <summary>Draws a row of face-down dice (one per die the player still holds), bouncing while a roll plays.</summary>
    public static void HiddenHand(int count, float size, bool dead = false, float gap = 3f, string? roll = null)
    {
        var t = 1f;
        var rolling = roll is not null && !dead && Fx.Rolling(roll, out t);

        for (var i = 0; i < count; i++)
        {
            var settled = !rolling || t >= SettleAt(i, count);
            HiddenDie(size, dead, settled ? 0f : Bounce(i, t, size));
            if (i < count - 1) ImGui.SameLine(0, gap);
        }
    }

    /// <summary>The dice settle left to right: the first past halfway, the last just before the end.</summary>
    private static float SettleAt(int index, int count) => 0.55f + 0.4f * index / MathF.Max(1f, count);

    /// <summary>A face that changes a dozen times a second, different for each die in the row.</summary>
    private static int Tumble(int index) => 1 + (int)Math.Floor((Fx.Now * 15.0 + index * 2.7) % 6.0);

    /// <summary>A bounce that dies down as the roll ends.</summary>
    private static float Bounce(int index, float t, float size) =>
        MathF.Abs(MathF.Sin((float)(Fx.Now * 11.0) + index * 1.3f)) * size * 0.35f * (1f - t);

    private static void DrawBody(ImDrawListPtr dl, Vector2 p, float size, Vector4 face, Vector4 border, float weight = 1f)
    {
        var p2 = p + new Vector2(size, size);
        var rounding = size * 0.18f;
        dl.AddRectFilled(p, p2, ImGui.GetColorU32(face), rounding);
        dl.AddRect(p, p2, ImGui.GetColorU32(border), rounding, ImDrawFlags.RoundCornersAll, MathF.Max(1f, size * 0.05f) * weight);
    }

    private static void DrawPips(ImDrawListPtr dl, Vector2 p, float size, int value)
    {
        var col = ImGui.GetColorU32(Pip);
        var r = size * 0.085f;
        // 3x3 grid fractions.
        const float a = 0.27f, b = 0.5f, c = 0.73f;

        void Dot(float fx, float fy) =>
            dl.AddCircleFilled(p + new Vector2(size * fx, size * fy), r, col);

        switch (value)
        {
            case 1: Dot(b, b); break;
            case 2: Dot(a, a); Dot(c, c); break;
            case 3: Dot(a, a); Dot(b, b); Dot(c, c); break;
            case 4: Dot(a, a); Dot(c, a); Dot(a, c); Dot(c, c); break;
            case 5: Dot(a, a); Dot(c, a); Dot(b, b); Dot(a, c); Dot(c, c); break;
            case 6: Dot(a, a); Dot(c, a); Dot(a, b); Dot(c, b); Dot(a, c); Dot(c, c); break;
        }
    }
}
