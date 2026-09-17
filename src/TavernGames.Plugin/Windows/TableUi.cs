using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Plugin.Game;

namespace TavernGames.Plugin.Windows;

/// <summary>Drawing helpers every game's table shares, so they all look like one plugin.</summary>
internal static class TableUi
{
    public static readonly Vector4 Gold = new(1f, 0.82f, 0.32f, 1f);
    public static readonly Vector4 TurnGreen = new(0.45f, 1f, 0.5f, 1f);
    public static readonly Vector4 Grey = new(0.5f, 0.5f, 0.5f, 1f);
    public static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    public static readonly Vector4 Cyan = new(0.5f, 0.9f, 1f, 1f);

    /// <summary>
    /// The table: one row per seat with a marker for whose turn it is. The active game
    /// draws each seat's tally (hidden dice, a score bar...) at the end of the row.
    /// </summary>
    public static void Roster(GameSession session)
    {
        var game = session.ActiveGame;
        var current = session.Phase == TavernGames.Core.GamePhase.Playing ? game?.CurrentPlayerId : null;

        ImGui.TextDisabled("At the table");
        foreach (var p in session.Players)
        {
            var isCurrent = p.Id == current && !p.Eliminated;
            var tag = p.Id == session.MyId ? " (you)" : p.IsBot ? " (bot)" : "";

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(isCurrent ? TurnGreen : Grey, isCurrent ? ">>" : "  ");
            ImGui.SameLine(0, 4);

            var nameColor = p.Eliminated ? Grey : isCurrent ? TurnGreen : Vector4.One;
            ImGui.TextColored(nameColor, p.Name + tag);
            ImGui.SameLine(0, 10);

            if (p.Eliminated)
                ImGui.TextDisabled("out");
            else if (game is not null)
                game.DrawSeatTally(p);
            else
                ImGui.TextDisabled(p.Tally.ToString());
        }
    }
}
