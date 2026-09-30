using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Platform;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// A game's "How to play", drawn from the rules its descriptor carries: each section's heading
/// in the display face, its text under it in the body face. The same drawing serves the game's
/// screen on the floor and the Rules popup at the table, so the two can never disagree.
/// </summary>
internal static class RulesPanel
{
    private const string PopupId = "##rulespopup";
    private static readonly Vector2 PopupBody = new(440f, 380f);

    private static GameDescriptor? _popupGame;

    /// <summary>
    /// Draws every section, wrapped to <paramref name="width"/>. Text is drawn unformatted under
    /// a pushed wrap position rather than with TextWrapped, which wraps at the window's edge and
    /// would run past a panel's padding.
    /// </summary>
    public static void Draw(GameDescriptor game, float width)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(80f, width));
        for (var i = 0; i < game.Rules.Count; i++)
        {
            var section = game.Rules[i];
            if (i > 0)
                ImGui.Dummy(new Vector2(0f, 4f));

            using (Theme.PushDisplay())
                ImGui.TextColored(Theme.Accent, section.Heading.ToUpperInvariant());

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            ImGui.TextUnformatted(section.Text);
            ImGui.PopStyleColor();
        }
        ImGui.PopTextWrapPos();
    }

    /// <summary>Opens the Rules popup for a game. Call <see cref="DrawPopup"/> every frame from the same window.</summary>
    public static void Open(GameDescriptor game)
    {
        _popupGame = game;
        ImGui.OpenPopup(PopupId);
    }

    /// <summary>The Rules popup at the table: the game's name, then its rules in a fixed-size, scrolling body.</summary>
    public static void DrawPopup()
    {
        if (!ImGui.BeginPopup(PopupId))
            return;

        if (_popupGame is { } game)
        {
            using (Theme.PushDisplay())
                ImGui.TextColored(Theme.Text, $"HOW TO PLAY {game.DisplayName.ToUpperInvariant()}");
            ImGui.Separator();

            if (ImGui.BeginChild("##rulesbody", PopupBody, false, ImGuiWindowFlags.None))
                Draw(game, ImGui.GetContentRegionAvail().X - 4f);
            ImGui.EndChild();
        }

        ImGui.EndPopup();
    }
}
