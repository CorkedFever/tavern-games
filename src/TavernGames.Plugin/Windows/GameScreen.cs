using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// A game's own screen on the floor: what it is, the rules the host can set, and the two ways
/// to play it. Against bots needs nothing and starts at once; opening a table needs a server
/// and gets a code for friends, or a venue to host it for.
/// </summary>
internal sealed class GameScreen(Plugin plugin, Action home)
{
    private GameDescriptor _game = GameCatalog.Games[0];
    private string? _hostVenueId;
    private float _turnDelaySec = 1.5f;
    private int _bots = 3;

    public GameDescriptor Game => _game;

    public void Open(GameDescriptor game, string? hostVenueId)
    {
        _game = game;
        _hostVenueId = hostVenueId;
        _turnDelaySec = plugin.Config.TurnDelayMs / 1000f;
        _bots = Math.Clamp(plugin.Config.LastBots, game.MinPlayers - 1, game.MaxPlayers - 1);
    }

    /// <summary>Straight into a game against bots with the remembered rules. The floor's "play again".</summary>
    public void PlayAgainstBots(GameDescriptor game, int bots)
    {
        _game = game;
        _turnDelaySec = plugin.Config.TurnDelayMs / 1000f;
        _bots = Math.Clamp(bots, game.MinPlayers - 1, game.MaxPlayers - 1);
        StartLocal();
    }

    public void Draw()
    {
        var game = _game;
        var online = plugin.Client.State == ConnectionState.Connected && !plugin.Client.IsLocal;

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(game.Blurb);
        ImGui.PopStyleColor();

        Theme.Heading("Table rules");
        foreach (var option in game.Options)
        {
            var key = $"{game.Type}.{option.Key}";
            var value = plugin.Config.GameOptions.TryGetValue(key, out var saved) ? saved : option.Default;
            value = Math.Clamp(value, option.Min, option.Max);
            ImGui.SetNextItemWidth(220f);
            if (ImGui.SliderInt($"{option.Label}##{key}", ref value, option.Min, option.Max))
                plugin.Config.GameOptions[key] = value;
        }

        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat("Game speed##speed", ref _turnDelaySec, 0.5f, 3.0f, "%.1f s a move");
        Ui.Hint("Higher is slower: bots think longer and rounds pause so everyone can read the reveal.");

        Theme.Heading("Play");
        var minBots = game.MinPlayers - 1;
        var maxBots = game.MaxPlayers - 1;
        _bots = Math.Clamp(_bots, minBots, maxBots);
        if (minBots < maxBots)
        {
            ImGui.SetNextItemWidth(220f);
            ImGui.SliderInt("Bots##bots", ref _bots, minBots, maxBots);
        }
        else
        {
            ImGui.TextColored(Theme.TextDim, minBots == 0 ? "Just you against the dealer." : $"{minBots} bots.");
        }

        ImGui.Dummy(new Vector2(0f, 4f));
        if (Ui.Key("Play vs bots", 200f, KeyStyle.Lit, true, "Right here, no server needed."))
            StartLocal();

        ImGui.SameLine();
        if (Ui.Key("Open a table", 200f, KeyStyle.Plain, online,
                online ? "Get a code for friends to sit down or watch." : "Connect to a server under Setup to play with other people, or to host for a venue."))
        {
            SaveSetup();
            plugin.Client.Send(new CreateRoom(plugin.ResolvePlayerName(), game.Type, OptionsFor(game), plugin.Config.TurnDelayMs, _hostVenueId));
        }

        if (online)
            DrawHostVenuePicker();

        ImGui.Dummy(new Vector2(0f, 2f));
        Ui.Hint(online
            ? "A table gets a 4-letter code. Friends enter it under Join a code; staff can host it for a venue so members find it on the venue page."
            : "Playing against bots needs no server. Opening a table does: it gets a 4-letter code for friends, and staff can host it for a venue.");
    }

    /// <summary>Staff and owners can open the table on behalf of one of their venues.</summary>
    private void DrawHostVenuePicker()
    {
        var hostable = plugin.Account.HostableVenues.ToList();
        if (_hostVenueId is not null && hostable.All(v => v.Id != _hostVenueId))
            _hostVenueId = null; // no longer staff there
        if (hostable.Count == 0)
            return;

        var current = hostable.FirstOrDefault(v => v.Id == _hostVenueId)?.Name ?? "Nobody (a private table)";
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("Host for", current))
        {
            if (ImGui.Selectable("Nobody (a private table)", _hostVenueId is null))
                _hostVenueId = null;
            foreach (var venue in hostable)
                if (ImGui.Selectable($"{venue.Name}##{venue.Id}", venue.Id == _hostVenueId))
                    _hostVenueId = venue.Id;
            ImGui.EndCombo();
        }

        if (_hostVenueId is not null)
            Ui.Hint("Members can join from the venue page, and the result counts on its leaderboard.");
    }

    private void StartLocal()
    {
        SaveSetup();
        plugin.Config.LastBots = _bots;
        plugin.Config.Save();
        plugin.Session.Reset();
        plugin.Client.StartLocal(_game.Type, OptionsFor(_game), plugin.Config.TurnDelayMs, plugin.ResolvePlayerName(), _bots);
        home();
    }

    private void SaveSetup()
    {
        plugin.Config.LastGameType = _game.Type;
        plugin.Config.TurnDelayMs = (int)(_turnDelaySec * 1000);
        plugin.Config.Save();
    }

    private Dictionary<string, int> OptionsFor(GameDescriptor game) =>
        game.Options.ToDictionary(
            o => o.Key,
            o => plugin.Config.GameOptions.TryGetValue($"{game.Type}.{o.Key}", out var v) ? v : o.Default);
}
