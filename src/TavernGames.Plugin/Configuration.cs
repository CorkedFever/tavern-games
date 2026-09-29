using Dalamud.Configuration;

namespace TavernGames.Plugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>WebSocket endpoint of the relay server.</summary>
    public string ServerUrl { get; set; } = "ws://localhost:5050/ws";

    /// <summary>Display name shown at tables. Defaults to the local character name when empty.</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>A short line under your name (e.g. "the masked gambler"). Flavor only.</summary>
    public string Tagline { get; set; } = "";

    /// <summary>Your own record on this device, per game type, counting offline games too.</summary>
    public Dictionary<string, int> LocalPlayed { get; set; } = new();
    public Dictionary<string, int> LocalWon { get; set; } = new();

    /// <summary>
    /// The secret each server issued to identify this install's profile, keyed by server URL.
    /// Anyone holding a token can act as that profile, so it never leaves this config file.
    /// </summary>
    public Dictionary<string, string> ProfileTokens { get; set; } = new();

    /// <summary>The game picked last time a table was opened or played against bots.</summary>
    public string LastGameType { get; set; } = "liarsdice";

    /// <summary>How many bots sat down last time you played offline, for "play again".</summary>
    public int LastBots { get; set; } = 3;

    /// <summary>Remembered per-game room options, keyed "gameType.optionKey".</summary>
    public Dictionary<string, int> GameOptions { get; set; } = new();

    /// <summary>Pause (ms) bots take per move; also paces the gap between rounds. Host-set per room.</summary>
    public int TurnDelayMs { get; set; } = 1500;

    /// <summary>Narrate each move to your own chat log for roleplay immersion (local echo only).</summary>
    public bool NarrateToChat { get; set; } = true;

    /// <summary>The window is folded down to just your seat.</summary>
    public bool WindowFolded { get; set; }

    /// <summary>Fold the window to your seat the moment a game starts, so the table stays out of the way.</summary>
    public bool FoldOnDeal { get; set; }

    /// <summary>Stage the action on the felt: stamps, tumbling dice, dealt cards, chips, confetti.</summary>
    public bool Effects { get; set; } = true;

    /// <summary>Play the table's sounds: your turn, a call, dice, cards, chips, the end of the game.</summary>
    public bool Sounds { get; set; } = true;

    /// <summary>How loud, 0 to 100. The sounds play outside the game's mixer, so this is the only knob.</summary>
    public int SoundVolume { get; set; } = 60;

    /// <summary>Adds a finished game to your device record.</summary>
    public void RecordLocalResult(string gameType, bool won)
    {
        LocalPlayed[gameType] = LocalPlayed.GetValueOrDefault(gameType) + 1;
        if (won) LocalWon[gameType] = LocalWon.GetValueOrDefault(gameType) + 1;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
