using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace LiarsDice.Plugin.Windows;

/// <summary>
/// Draws dice as actual faces with pips via ImGui's draw list, so the UI shows
/// real dice instead of "[2]". Hidden dice (other players' hands) are drawn
/// face-down so you can see how many each player holds without their values.
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

    /// <summary>Draws a single face-up die showing <paramref name="value"/> and advances the layout.</summary>
    public static void Die(int value, float size)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        DrawBody(dl, p, size, Face, Border);
        DrawPips(dl, p, size, value);
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>Draws a face-down die (count is known, value hidden) and advances the layout.</summary>
    public static void HiddenDie(float size, bool dead = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        DrawBody(dl, p, size, dead ? DeadFace : HiddenFace, dead ? DeadBorder : HiddenBorder);
        if (!dead)
        {
            // A faint diamond mark hints at a concealed die.
            var c = p + new Vector2(size * 0.5f, size * 0.5f);
            var d = size * 0.17f;
            var col = ImGui.GetColorU32(HiddenMark);
            dl.AddQuadFilled(c + new Vector2(0, -d), c + new Vector2(d, 0), c + new Vector2(0, d), c + new Vector2(-d, 0), col);
        }
        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>Draws a row of face-up dice.</summary>
    public static void Hand(int[] dice, float size, float gap = 5f)
    {
        for (var i = 0; i < dice.Length; i++)
        {
            Die(dice[i], size);
            if (i < dice.Length - 1) ImGui.SameLine(0, gap);
        }
    }

    /// <summary>Draws a row of face-down dice (one per die the player still holds).</summary>
    public static void HiddenHand(int count, float size, bool dead = false, float gap = 3f)
    {
        for (var i = 0; i < count; i++)
        {
            HiddenDie(size, dead);
            if (i < count - 1) ImGui.SameLine(0, gap);
        }
    }

    private static void DrawBody(ImDrawListPtr dl, Vector2 p, float size, Vector4 face, Vector4 border)
    {
        var p2 = p + new Vector2(size, size);
        var rounding = size * 0.18f;
        dl.AddRectFilled(p, p2, ImGui.GetColorU32(face), rounding);
        dl.AddRect(p, p2, ImGui.GetColorU32(border), rounding, ImDrawFlags.RoundCornersAll, MathF.Max(1f, size * 0.05f));
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
