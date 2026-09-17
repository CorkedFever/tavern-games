using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TavernGames.Core;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// The shell every game shares: connect, pick a game and open or join a room, the
/// room lobby, game over, and the log. While a game is being played the active
/// <see cref="Game.IClientGame"/> draws the table.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly Plugin _plugin;

    // Form fields persisted across frames.
    private string _serverUrl;
    private string _playerName;
    private string _gameType;
    private float _turnDelaySec;
    private string _joinCode = "";
    private string? _hostVenueId;   // host the next table for this venue (null = private table)
    private bool _jumpToPlayTab;

    private readonly ProfileTab _profileTab;
    private readonly VenuesTab _venuesTab;

    public MainWindow(Plugin plugin) : base("Tavern Games##TavernGamesMain")
    {
        _plugin = plugin;
        _profileTab = new ProfileTab(plugin);
        _venuesTab = new VenuesTab(plugin, venueId =>
        {
            _hostVenueId = venueId;
            _jumpToPlayTab = true;
        });
        _serverUrl = plugin.Config.ServerUrl;
        _playerName = plugin.Config.PlayerName;
        _gameType = GameCatalog.Find(plugin.Config.LastGameType)?.Type ?? GameCatalog.Games[0].Type;
        _turnDelaySec = plugin.Config.TurnDelayMs / 1000f;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 320),
            MaximumSize = new Vector2(900, 1200),
        };
        Size = new Vector2(440, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Game.GameClient Client => _plugin.Client;
    private Game.GameSession Session => _plugin.Session;

    private string _lastDrawError = "";

    public override void Draw() => Guarded("window", DrawContent);

    /// <summary>
    /// Runs one section of the UI so that a bug in it can't take the window down. Each
    /// section is guarded from INSIDE whatever ImGui scope contains it (a tab item, the
    /// window), so when it throws, the enclosing End call still runs and ImGui's stack
    /// stays balanced. The same error is logged once, not once per frame.
    /// </summary>
    private void Guarded(string section, Action draw)
    {
        try
        {
            draw();
        }
        catch (Exception ex)
        {
            var signature = $"{section}: {ex.GetType().Name}: {ex.Message}";
            if (signature != _lastDrawError)
            {
                _lastDrawError = signature;
                Plugin.Log.Error(ex, "Tavern Games: UI error while drawing {Section}.", section);
            }
            ImGui.TextColored(TableUi.Red, "A UI error occurred here. See /xllog for details.");
        }
    }

    private void DrawContent()
    {
        if (Client.State != Game.ConnectionState.Connected)
        {
            DrawConnect();
            return;
        }

        if (!Session.InRoom)
        {
            DrawLobbyEntry();
        }
        else
        {
            DrawRoomHeader();

            switch (Session.Phase)
            {
                case GamePhase.Lobby:
                    DrawRoomLobby();
                    break;
                case GamePhase.Playing when Session.ActiveGame is { } game:
                    Guarded(game.GameType, () => game.DrawTable(Session, Client.Send));
                    break;
                case GamePhase.Playing:
                    ImGui.TextColored(TableUi.Red, $"This plugin version doesn't know the game '{Session.GameType}'. Update to play it.");
                    break;
                case GamePhase.GameOver:
                    DrawGameOver();
                    break;
            }

            if (Session.Phase != GamePhase.Lobby)
            {
                ImGui.Spacing();
                if (ImGui.Button(Session.IsSpectator ? "Stop watching" : "Leave")) LeaveRoom();
            }
        }

        ImGui.Separator();
        DrawLog();
    }

    private void DrawConnect()
    {
        ImGui.TextWrapped("Connect to a Tavern Games server to open or join a table.");
        ImGui.Spacing();

        ImGui.InputText("Server", ref _serverUrl, 256);
        ImGui.InputText("Name", ref _playerName, 24);
        ImGui.TextDisabled("Leave Name blank to use your character name.");
        DrawNarrationToggle();
        ImGui.Spacing();

        if (Client.State == Game.ConnectionState.Connecting)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.8f, 0.2f, 1f), "Connecting...");
        }
        else if (ImGui.Button("Connect"))
        {
            _plugin.Config.ServerUrl = _serverUrl;
            _plugin.Config.PlayerName = _playerName;
            _plugin.Config.Save();
            _ = Client.ConnectAsync(_serverUrl);
        }

        if (!string.IsNullOrEmpty(Client.LastError))
        {
            ImGui.Spacing();
            ImGui.TextColored(TableUi.Red, $"Error: {Client.LastError}");
        }
    }

    private void DrawLobbyEntry()
    {
        var profile = _plugin.Account.Profile;
        ImGui.TextUnformatted(profile is null ? "Connected as a guest." : $"Connected as {profile.DisplayName}.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Disconnect")) _ = Client.DisconnectAsync();

        if (!ImGui.BeginTabBar("##lobbytabs")) return;

        var playFlags = _jumpToPlayTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        _jumpToPlayTab = false;
        if (ImGui.BeginTabItem("Play", playFlags))
        {
            Guarded("play tab", DrawPlayTab);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Venues"))
        {
            Guarded("venues tab", _venuesTab.Draw);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Profile"))
        {
            Guarded("profile tab", _profileTab.Draw);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawPlayTab()
    {
        ImGui.TextUnformatted("Open a new table");
        var game = GameCatalog.Find(_gameType) ?? GameCatalog.Games[0];

        if (ImGui.BeginCombo("Game", game.DisplayName))
        {
            foreach (var candidate in GameCatalog.Games)
            {
                if (ImGui.Selectable(candidate.DisplayName, candidate.Type == game.Type))
                    _gameType = candidate.Type;
            }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled($"{game.Blurb} ({game.MinPlayers}-{game.MaxPlayers} players)");

        // Each game declares its own room options; they all render as sliders.
        foreach (var option in game.Options)
        {
            var key = $"{game.Type}.{option.Key}";
            var value = _plugin.Config.GameOptions.TryGetValue(key, out var saved) ? saved : option.Default;
            value = Math.Clamp(value, option.Min, option.Max);
            if (ImGui.SliderInt($"{option.Label}##{key}", ref value, option.Min, option.Max))
                _plugin.Config.GameOptions[key] = value;
        }

        ImGui.SliderFloat("Game speed (sec/move)", ref _turnDelaySec, 0.5f, 3.0f, "%.1f s");
        ImGui.TextDisabled("Higher = slower bots and longer pauses between rounds.");
        DrawHostVenuePicker();

        if (ImGui.Button("Create Room"))
        {
            var options = game.Options.ToDictionary(
                o => o.Key,
                o => _plugin.Config.GameOptions.TryGetValue($"{game.Type}.{o.Key}", out var v) ? v : o.Default);

            _plugin.Config.LastGameType = game.Type;
            _plugin.Config.TurnDelayMs = (int)(_turnDelaySec * 1000);
            _plugin.Config.Save();
            Client.Send(new CreateRoom(_plugin.ResolvePlayerName(), game.Type, options, _plugin.Config.TurnDelayMs, _hostVenueId));
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Join an existing table");
        ImGui.InputText("Room code", ref _joinCode, 8);
        ImGui.SameLine();
        if (ImGui.Button("Join") && _joinCode.Trim().Length > 0)
            Client.Send(new JoinRoom(_joinCode.Trim(), _plugin.ResolvePlayerName()));
        ImGui.SameLine();
        if (ImGui.Button("Spectate") && _joinCode.Trim().Length > 0)
            Client.Send(new Spectate(_joinCode.Trim()));
        ImGui.TextDisabled("Join to play, or Spectate to just watch the table.");
    }

    /// <summary>Staff and owners can open the table on behalf of one of their venues.</summary>
    private void DrawHostVenuePicker()
    {
        var hostable = _plugin.Account.HostableVenues.ToList();
        if (_hostVenueId is not null && hostable.All(v => v.Id != _hostVenueId))
            _hostVenueId = null; // no longer staff there
        if (hostable.Count == 0) return;

        var current = hostable.FirstOrDefault(v => v.Id == _hostVenueId)?.Name ?? "Nobody (private table)";
        if (ImGui.BeginCombo("Host for", current))
        {
            if (ImGui.Selectable("Nobody (private table)", _hostVenueId is null))
                _hostVenueId = null;
            foreach (var venue in hostable)
                if (ImGui.Selectable($"{venue.Name}##{venue.Id}", venue.Id == _hostVenueId))
                    _hostVenueId = venue.Id;
            ImGui.EndCombo();
        }
        if (_hostVenueId is not null)
            ImGui.TextDisabled("Members can join from the venue page, and the result counts on its leaderboard.");
    }

    /// <summary>Which game, which room, and whether you're only watching.</summary>
    private void DrawRoomHeader()
    {
        var name = GameCatalog.Find(Session.GameType)?.DisplayName ?? Session.GameType;
        ImGui.TextColored(TableUi.Gold, name);
        ImGui.SameLine();
        ImGui.TextDisabled("room");
        ImGui.SameLine();
        ImGui.TextColored(TableUi.Cyan, Session.RoomCode);
        if (Session.IsSpectator)
        {
            ImGui.SameLine();
            ImGui.TextColored(TableUi.Cyan, "(spectating)");
        }
        if (Session.VenueName is { } venueName)
            ImGui.TextDisabled($"Hosted by {venueName}");
        ImGui.Separator();
    }

    private void DrawRoomLobby()
    {
        var game = GameCatalog.Find(Session.GameType);
        var minPlayers = game?.MinPlayers ?? 2;
        var maxPlayers = game?.MaxPlayers ?? 6;

        ImGui.TextDisabled("Share the room code so others can join or spectate.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Players ({Session.Players.Count}/{maxPlayers}):");
        foreach (var p in Session.Players)
        {
            var you = p.Id == Session.MyId ? " (you)" : "";
            var host = p.Id == Session.HostId ? " [host]" : "";
            var bot = p.IsBot ? " (bot)" : "";
            ImGui.BulletText($"{p.Name}{you}{host}{bot}");
            if (Session.IsHost && p.IsBot)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##{p.Id}"))
                    Client.Send(new RemoveBot(p.Id));
            }
        }

        ImGui.Spacing();
        if (Session.IsHost)
        {
            ImGui.BeginDisabled(Session.Players.Count >= maxPlayers);
            if (ImGui.Button("Add Bot"))
                Client.Send(new AddBot());
            ImGui.EndDisabled();
            ImGui.SameLine();

            var canStart = Session.Players.Count >= minPlayers;
            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("Start Game"))
                Client.Send(new StartGame());
            ImGui.EndDisabled();
            if (!canStart)
                ImGui.TextDisabled($"Need at least {minPlayers} players. Add a bot to play solo.");
        }
        else
        {
            ImGui.TextDisabled("Waiting for the host to start...");
        }

        if (ImGui.Button(Session.IsSpectator ? "Stop watching" : "Leave")) LeaveRoom();
    }

    private void DrawGameOver()
    {
        var winner = Session.WinnerId is { } w ? Session.NameOf(w) : "nobody";
        ImGui.TextColored(TableUi.Gold, $"{winner} wins!");
        ImGui.Spacing();
        TableUi.Roster(Session);
    }

    private void DrawLog()
    {
        ImGui.TextDisabled("Log");
        if (ImGui.BeginChild("##log", new Vector2(0, 120), true))
        {
            foreach (var line in Session.Log)
                ImGui.TextWrapped(line);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    private void DrawNarrationToggle()
    {
        var narrate = _plugin.Config.NarrateToChat;
        if (ImGui.Checkbox("Narrate moves to my chat log", ref narrate))
        {
            _plugin.Config.NarrateToChat = narrate;
            _plugin.Config.Save();
        }
    }

    private void LeaveRoom()
    {
        Client.Send(new LeaveRoom());
        Session.Reset();
    }
}
