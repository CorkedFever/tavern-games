using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Cards;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// Draws playing cards on ImGui's draw list. Suit symbols aren't reliably present in
/// the game's font, so the pips are built from circles, triangles and quads instead;
/// only the rank is text. A code of <see cref="Card.HiddenCode"/> draws a card back.
/// </summary>
internal static class CardRenderer
{
    private const float Aspect = 1.4f; // height / width

    private static readonly Vector4 Face = new(0.97f, 0.96f, 0.92f, 1f);
    private static readonly Vector4 Border = new(0.30f, 0.27f, 0.24f, 1f);
    private static readonly Vector4 Black = new(0.12f, 0.11f, 0.13f, 1f);
    private static readonly Vector4 RedSuit = new(0.78f, 0.13f, 0.16f, 1f);
    private static readonly Vector4 Back = new(0.42f, 0.15f, 0.17f, 1f);
    private static readonly Vector4 BackTrim = new(0.80f, 0.62f, 0.30f, 1f);
    private static readonly Vector4 Highlight = new(1f, 0.78f, 0.2f, 1f);

    /// <summary>Draws one card from its wire code and advances the layout.</summary>
    public static void Draw(string code, float width, bool highlight = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, width * Aspect);
        var rounding = width * 0.12f;

        if (!Card.TryParse(code, out var card))
        {
            DrawBack(dl, p, size, rounding);
            ImGui.Dummy(size);
            return;
        }

        dl.AddRectFilled(p, p + size, ImGui.GetColorU32(Face), rounding);
        dl.AddRect(p, p + size, ImGui.GetColorU32(highlight ? Highlight : Border), rounding,
            ImDrawFlags.RoundCornersAll, highlight ? 2.4f : 1.2f);

        var ink = ImGui.GetColorU32(card.Suit is Suit.Hearts or Suit.Diamonds ? RedSuit : Black);

        // Rank in the corner, a small pip under it, and a large pip in the body.
        dl.AddText(p + new Vector2(width * 0.10f, width * 0.04f), ink, RankLabel(card.Rank));
        DrawSuit(dl, card.Suit, p + new Vector2(width * 0.50f, size.Y * 0.66f), width * 0.13f, ink);

        ImGui.Dummy(size);
    }

    /// <summary>Draws a row of cards; hidden codes show as card backs.</summary>
    public static void Hand(IReadOnlyList<string> codes, float width, float gap = 4f)
    {
        for (var i = 0; i < codes.Count; i++)
        {
            Draw(codes[i], width);
            if (i < codes.Count - 1) ImGui.SameLine(0, gap);
        }
    }

    public static float HeightFor(float width) => width * Aspect;

    private static void DrawBack(ImDrawListPtr dl, Vector2 p, Vector2 size, float rounding)
    {
        dl.AddRectFilled(p, p + size, ImGui.GetColorU32(Back), rounding);
        dl.AddRect(p, p + size, ImGui.GetColorU32(Border), rounding, ImDrawFlags.RoundCornersAll, 1.2f);

        var inset = new Vector2(size.X * 0.14f, size.X * 0.14f);
        dl.AddRect(p + inset, p + size - inset, ImGui.GetColorU32(BackTrim), rounding * 0.5f, ImDrawFlags.RoundCornersAll, 1f);

        var c = p + size * 0.5f;
        var d = size.X * 0.16f;
        dl.AddQuadFilled(c + new Vector2(0, -d), c + new Vector2(d, 0), c + new Vector2(0, d), c + new Vector2(-d, 0),
            ImGui.GetColorU32(BackTrim));
    }

    /// <summary>Draws a suit pip centred on <paramref name="c"/>; <paramref name="r"/> is the lobe radius.</summary>
    private static void DrawSuit(ImDrawListPtr dl, Suit suit, Vector2 c, float r, uint ink)
    {
        switch (suit)
        {
            case Suit.Diamonds:
                dl.AddQuadFilled(
                    c + new Vector2(0, -2.1f * r), c + new Vector2(1.5f * r, 0),
                    c + new Vector2(0, 2.1f * r), c + new Vector2(-1.5f * r, 0), ink);
                break;

            case Suit.Hearts:
                dl.AddCircleFilled(c + new Vector2(-0.95f * r, -0.7f * r), r, ink);
                dl.AddCircleFilled(c + new Vector2(0.95f * r, -0.7f * r), r, ink);
                dl.AddTriangleFilled(
                    c + new Vector2(-1.88f * r, -0.25f * r), c + new Vector2(1.88f * r, -0.25f * r),
                    c + new Vector2(0, 2.0f * r), ink);
                break;

            case Suit.Spades:
                dl.AddCircleFilled(c + new Vector2(-0.95f * r, 0.35f * r), r, ink);
                dl.AddCircleFilled(c + new Vector2(0.95f * r, 0.35f * r), r, ink);
                dl.AddTriangleFilled(
                    c + new Vector2(-1.88f * r, -0.1f * r), c + new Vector2(1.88f * r, -0.1f * r),
                    c + new Vector2(0, -2.2f * r), ink);
                DrawStem(dl, c + new Vector2(0, 0.6f * r), r, ink);
                break;

            case Suit.Clubs:
                dl.AddCircleFilled(c + new Vector2(0, -1.0f * r), r, ink);
                dl.AddCircleFilled(c + new Vector2(-1.05f * r, 0.45f * r), r, ink);
                dl.AddCircleFilled(c + new Vector2(1.05f * r, 0.45f * r), r, ink);
                DrawStem(dl, c + new Vector2(0, 0.2f * r), r, ink);
                break;
        }
    }

    private static void DrawStem(ImDrawListPtr dl, Vector2 top, float r, uint ink) =>
        dl.AddTriangleFilled(top, top + new Vector2(-0.8f * r, 1.7f * r), top + new Vector2(0.8f * r, 1.7f * r), ink);

    private static string RankLabel(Rank rank) => rank switch
    {
        Rank.Ace => "A",
        Rank.King => "K",
        Rank.Queen => "Q",
        Rank.Jack => "J",
        _ => ((int)rank).ToString(),
    };
}
