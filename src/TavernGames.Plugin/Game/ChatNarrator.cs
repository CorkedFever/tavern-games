using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Game;

/// <summary>
/// Turns server messages into flavored, in-character lines for the local chat log.
/// Pure text only: it prints nothing to other players. Room-level events are worded
/// here; each game words its own moves. Returns null for messages that stay quiet.
/// </summary>
public static class ChatNarrator
{
    public static string? BuildLine(NetMessage message, GameSession session) => message switch
    {
        GameEnded g => $"{session.NameOf(g.WinnerId)} takes the table. Victory!",
        RemovedFromRoom r => $"[Tavern Games] {r.Reason}",
        ErrorMessage e => $"[Tavern Games] {e.Text}",
        _ => session.ActiveGame?.Narrate(message, session),
    };
}
