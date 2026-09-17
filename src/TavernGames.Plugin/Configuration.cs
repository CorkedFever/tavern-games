using Dalamud.Configuration;

namespace TavernGames.Plugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>WebSocket endpoint of the relay server.</summary>
    public string ServerUrl { get; set; } = "ws://localhost:5050/ws";

    /// <summary>Display name shown to other players. Defaults to the local character name when empty.</summary>
    public string PlayerName { get; set; } = "";

    public int StartingDice { get; set; } = 5;

    /// <summary>Pause (ms) bots take per move; also paces the gap between rounds. Host-set per room.</summary>
    public int TurnDelayMs { get; set; } = 1500;

    /// <summary>Narrate each move to your own chat log for roleplay immersion (local echo only).</summary>
    public bool NarrateToChat { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
