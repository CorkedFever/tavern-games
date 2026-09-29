using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Cards;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// Draws playing cards on ImGui's draw list. Suit symbols aren't reliably present in
/// the game's font, so the pips are built from circles, triangles and quads instead;
/// only the rank is text. A code of <see cref="Card.HiddenCode"/> draws a card back.
/// Given a deal or flip key that <see cref="Fx"/> has started, a hand's cards arrive one
/// after another, or turn over from back to face.
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

    /// <summary>
    /// Draws one card from its wire code and advances the layout. <paramref name="lift"/> and
    /// <paramref name="alpha"/> are for a card still arriving; <paramref name="flip"/> runs 0
    /// (face down) to 1 (face up) as a card turns over.
    /// </summary>
    public static void Draw(string code, float width, bool highlight = false, float alpha = 1f, float lift = 0f, float flip = 1f)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, width * Aspect);
        var rounding = width * 0.12f;

        // A flip is a horizontal squash: the back until halfway, the face after.
        var squash = MathF.Abs(MathF.Cos(flip * MathF.PI));
        var drawWidth = MathF.Max(2f, width * squash);
        var q = p + new Vector2((width - drawWidth) / 2f, -lift);
        var drawSize = new Vector2(drawWidth, size.Y);

        if (!Card.TryParse(code, out var card) || flip < 0.5f)
        {
            DrawBack(dl, q, drawSize, rounding, alpha);
            ImGui.Dummy(size);
            return;
        }

        dl.AddRectFilled(q, q + drawSize, Col(Face, alpha), rounding);
        dl.AddRect(q, q + drawSize, Col(highlight ? Highlight : Border, alpha), rounding,
            ImDrawFlags.RoundCornersAll, highlight ? 2.4f : 1.2f);

        // Mid-turn the card is too narrow for its markings.
        if (squash > 0.6f)
        {
            var ink = Col(card.Suit is Suit.Hearts or Suit.Diamonds ? RedSuit : Black, alpha);

            // Rank in the corner, a large pip in the body.
            dl.AddText(q + new Vector2(width * 0.10f, width * 0.04f), ink, RankLabel(card.Rank));
            DrawSuit(dl, card.Suit, q + new Vector2(drawWidth * 0.50f, size.Y * 0.66f), width * 0.13f, ink);
        }

        ImGui.Dummy(size);
    }

    /// <summary>
    /// Draws a row of cards; hidden codes show as card backs. With a <paramref name="deal"/> key
    /// the cards arrive one after another; with a <paramref name="flip"/> key the cards from
    /// <paramref name="flipFrom"/> on turn over.
    /// </summary>
    public static void Hand(IReadOnlyList<string> codes, float width, float gap = 4f, string? deal = null, string? flip = null, int flipFrom = 0)
    {
        for (var i = 0; i < codes.Count; i++)
        {
            var arrived = deal is null ? 1f : Fx.DealProgress(deal, i);
            var turned = flip is null || i < flipFrom ? 1f : Fx.FlipProgress(flip, i - flipFrom);
            Draw(codes[i], width, alpha: 0.15f + 0.85f * arrived, lift: (1f - EaseOut(arrived)) * 16f, flip: turned);
            if (i < codes.Count - 1) ImGui.SameLine(0, gap);
        }
    }

    public static float HeightFor(float width) => width * Aspect;

    private static void DrawBack(ImDrawListPtr dl, Vector2 p, Vector2 size, float rounding, float alpha)
    {
        dl.AddRectFilled(p, p + size, Col(Back, alpha), rounding);
        dl.AddRect(p, p + size, Col(Border, alpha), rounding, ImDrawFlags.RoundCornersAll, 1.2f);

        if (size.X < 12f)
            return;

        var inset = new Vector2(size.X * 0.14f, size.X * 0.14f);
        dl.AddRect(p + inset, p + size - inset, Col(BackTrim, alpha), rounding * 0.5f, ImDrawFlags.RoundCornersAll, 1f);

        var c = p + size * 0.5f;
        var d = size.X * 0.16f;
        dl.AddQuadFilled(c + new Vector2(0, -d), c + new Vector2(d, 0), c + new Vector2(0, d), c + new Vector2(-d, 0),
            Col(BackTrim, alpha));
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

    private static uint Col(Vector4 colour, float alpha) => ImGui.GetColorU32(colour with { W = colour.W * alpha });

    private static float EaseOut(float k) => 1f - MathF.Pow(1f - k, 3f);
}
