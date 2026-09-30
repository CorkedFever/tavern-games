using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Plugin.Game;

namespace TavernGames.Plugin.Windows;

/// <summary>The server, and the few things about the table that are yours to set.</summary>
internal sealed class SetupApp(Plugin plugin)
{
    private string _serverUrl = "";
    private bool _loaded;

    public void Draw()
    {
        var config = plugin.Config;
        var client = plugin.Client;
        if (!_loaded)
        {
            _serverUrl = config.ServerUrl;
            _loaded = true;
        }

        Theme.Heading("Server");
        Ui.Hint("Playing against bots needs no server. The tavern's own server is for playing with other people: tables with codes, venues and leaderboards. It's set already; press Connect.");

        var online = client.State == ConnectionState.Connected && !client.IsLocal;
        var connecting = client.State == ConnectionState.Connecting;

        ImGui.SetNextItemWidth(300f);
        ImGui.BeginDisabled(online || connecting);
        var submitted = ImGui.InputTextWithHint("##server", "wss://host/path", ref _serverUrl, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.EndDisabled();
        Ui.Tip("The relay's WebSocket address. Leave it on the tavern's server unless you run your own relay.");

        ImGui.SameLine();
        if (online)
        {
            if (ImGui.Button("Disconnect"))
                _ = client.DisconnectAsync();
        }
        else
        {
            ImGui.BeginDisabled(connecting);
            if ((ImGui.Button("Connect") || submitted) && !connecting)
            {
                config.ServerUrl = _serverUrl.Trim();
                config.Save();
                _ = client.ConnectAsync(config.ServerUrl);
            }
            ImGui.EndDisabled();
        }

        var (led, status) = client.State switch
        {
            ConnectionState.Connecting => (Theme.Accent, "connecting..."),
            ConnectionState.Connected when online => (Theme.Good, plugin.Account.Profile is { } p ? $"connected as {p.DisplayName}" : "connected as a guest"),
            _ => (Theme.TextFaint, "not connected"),
        };
        Ui.Dot(led);
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(Theme.TextDim, status);

        if (client.LastError is { } error && client.State == ConnectionState.Disconnected)
            ImGui.TextColored(Theme.Bad, Ui.Ellipsis(error, 90));

        if (!online && !connecting && _serverUrl.Trim() != Configuration.DefaultServerUrl)
        {
            if (ImGui.SmallButton("Use the tavern's server"))
                _serverUrl = Configuration.DefaultServerUrl;
            Ui.Tip(Configuration.DefaultServerUrl);
        }

        Ui.Hint("The first time you connect, the server gives this install a profile it will recognise again. There is no account and no password.");

        Theme.Heading("At the table");
        var narrate = config.NarrateToChat;
        if (ImGui.Checkbox("Narrate moves to my chat log", ref narrate))
        {
            config.NarrateToChat = narrate;
            config.Save();
        }
        Ui.Tip("Every move as a line in your own chat log, in character. Nobody else sees it.");

        var fold = config.FoldOnDeal;
        if (ImGui.Checkbox("Fold the window to my seat when a game starts", ref fold))
        {
            config.FoldOnDeal = fold;
            config.Save();
        }
        Ui.Tip("Your seat alone, in a corner: your dice, your keys, and the last thing that happened. Unfold any time to see the table.");

        var effects = config.Effects;
        if (ImGui.Checkbox("Show the action on the felt", ref effects))
        {
            config.Effects = effects;
            config.Save();
        }
        Ui.Tip("Dice that tumble, cards that deal, chips that fly, and the big words: LIAR!, BUST!, VICTORY. Off, the table just updates.");

        var sounds = config.Sounds;
        if (ImGui.Checkbox("Sound effects", ref sounds))
        {
            config.Sounds = sounds;
            config.Save();
        }
        Ui.Tip("The table's own sounds: dice, cards, chips, a chime when it's your turn, a fanfare at the end. Made by the plugin, so nothing sounds like a tell.");

        var volume = config.SoundVolume;
        ImGui.SetNextItemWidth(220f);
        ImGui.SliderInt("Volume", ref volume, 0, 100, "%d%%");
        if (volume != config.SoundVolume)
            config.SoundVolume = volume;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            // Rebuilt and previewed once the slider is let go, not on every pixel it moves.
            config.Save();
            Sound.Volume = config.SoundVolume / 100f;
            Sound.Preview(Sound.Cue.Chips);
        }
        Ui.Tip("They play outside the game's mixer, so this is the only knob for them.");

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextFaint, "Hear them:");
        foreach (var (cue, label) in new[]
                 {
                     (Sound.Cue.Turn, "Turn"), (Sound.Cue.Alert, "Call"), (Sound.Cue.Dice, "Dice"), (Sound.Cue.Deal, "Deal"),
                     (Sound.Cue.Chips, "Chips"), (Sound.Cue.Spin, "Wheel"), (Sound.Cue.Victory, "Win"), (Sound.Cue.Defeat, "Lose"),
                 })
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(label))
                Sound.Preview(cue);
        }

        if (Sound.Directory is { } folder)
        {
            Ui.Hint($"To use your own, put a 16-bit WAV named turn, alert, dice, deal, chips, victory or defeat (.wav) in {folder}");
            if (ImGui.SmallButton("Copy folder path"))
                ImGui.SetClipboardText(folder);
            ImGui.SameLine();
            if (ImGui.SmallButton("Reload sounds"))
                Sound.Reload();
            Ui.Tip("Picks up files you have added or changed.");
        }

        ImGui.Dummy(new Vector2(0f, 4f));
        Ui.Hint("Your name and tagline are under Profile.");
    }
}
