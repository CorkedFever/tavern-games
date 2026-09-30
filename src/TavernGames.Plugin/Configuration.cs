using Dalamud.Configuration;

namespace TavernGames.Plugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>The tavern's own relay, behind meteor's Caddy at /party.</summary>
    public const string DefaultServerUrl = "wss://tavern-games.corkedfever.com/party/ws";

    /// <summary>What 0.1.0 shipped as the default: a relay on the player's own machine.</summary>
    private const string OldLocalDefault = "ws://localhost:5050/ws";

    /// <summary>2: the default server moved from localhost to the tavern's public relay.</summary>
    public int Version { get; set; } = 2;

    /// <summary>WebSocket endpoint of the relay server.</summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

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

    /// <summary>
    /// Brings a config saved by an older version up to date. Returns true when something
    /// changed and the config should be saved. Runs once per version step, so a player who
    /// later points the plugin back at their own localhost relay on purpose keeps that choice.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= 2)
            return false;

        // A saved localhost address is almost always 0.1.0's default rather than a choice.
        if (string.Equals(ServerUrl.Trim(), OldLocalDefault, StringComparison.OrdinalIgnoreCase))
            ServerUrl = DefaultServerUrl;
        Version = 2;
        return true;
    }

    /// <summary>Adds a finished game to your device record.</summary>
    public void RecordLocalResult(string gameType, bool won)
    {
        LocalPlayed[gameType] = LocalPlayed.GetValueOrDefault(gameType) + 1;
        if (won) LocalWon[gameType] = LocalWon.GetValueOrDefault(gameType) + 1;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
