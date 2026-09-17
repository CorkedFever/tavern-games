using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LiarsDice.Core;
using LiarsDice.Core.Protocol;

namespace LiarsDice.Plugin.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin _plugin;

    // Form fields persisted across frames.
    private string _serverUrl;
    private string _playerName;
    private int _startingDice;
    private float _turnDelaySec;
    private string _joinCode = "";
    private int _bidQuantity = 1;
    private int _bidFace = 2;

    public MainWindow(Plugin plugin) : base("Liar's Dice##LiarsDiceMain")
    {
        _plugin = plugin;
        _serverUrl = plugin.Config.ServerUrl;
        _playerName = plugin.Config.PlayerName;
        _startingDice = plugin.Config.StartingDice;
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

    public override void Draw()
    {
        try
        {
            DrawContent();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Liar's Dice: UI draw error.");
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "A UI error occurred — see /xllog for details.");
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
            if (Session.IsSpectator)
                ImGui.TextColored(new Vector4(0.5f, 0.9f, 1f, 1f), "Spectating — watching only");

            switch (Session.Phase)
            {
                case GamePhase.Lobby: DrawRoomLobby(); break;
                case GamePhase.Bidding: DrawGame(); break;
                case GamePhase.GameOver: DrawGameOver(); break;
            }
        }

        ImGui.Separator();
        DrawLog();
    }

    private void DrawConnect()
    {
        ImGui.TextWrapped("Connect to a Liar's Dice relay server to create or join a game.");
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
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Error: {Client.LastError}");
        }
    }

    private void DrawLobbyEntry()
    {
        ImGui.Text("Connected.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Disconnect")) _ = Client.DisconnectAsync();
        ImGui.Separator();

        ImGui.TextUnformatted("Create a new game");
        ImGui.SliderInt("Dice per player", ref _startingDice, 1, 6);
        ImGui.SliderFloat("Game speed (sec/move)", ref _turnDelaySec, 0.5f, 3.0f, "%.1f s");
        ImGui.TextDisabled("Higher = slower bots and a longer pause between rounds.");
        if (ImGui.Button("Create Room"))
        {
            _plugin.Config.StartingDice = _startingDice;
            _plugin.Config.TurnDelayMs = (int)(_turnDelaySec * 1000);
            _plugin.Config.Save();
            Client.Send(new CreateRoom(_plugin.ResolvePlayerName(), _startingDice, _plugin.Config.TurnDelayMs));
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Join an existing game");
        ImGui.InputText("Room code", ref _joinCode, 8);
        ImGui.SameLine();
        if (ImGui.Button("Join") && _joinCode.Trim().Length > 0)
            Client.Send(new JoinRoom(_joinCode.Trim(), _plugin.ResolvePlayerName()));
        ImGui.SameLine();
        if (ImGui.Button("Spectate") && _joinCode.Trim().Length > 0)
            Client.Send(new Spectate(_joinCode.Trim()));
        ImGui.TextDisabled("Join to play, or Spectate to just watch the table.");
    }

    private void DrawRoomLobby()
    {
        ImGui.Text($"Room ");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 1f, 1f), Session.RoomCode);
        ImGui.SameLine();
        ImGui.TextDisabled("(share this code)");

        ImGui.Spacing();
        ImGui.TextUnformatted($"Players ({Session.Players.Count}/{LiarsDiceGame.MaxPlayers}):");
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
            var roomFull = Session.Players.Count >= LiarsDiceGame.MaxPlayers;
            ImGui.BeginDisabled(roomFull);
            if (ImGui.Button("Add Bot"))
                Client.Send(new AddBot());
            ImGui.EndDisabled();
            ImGui.SameLine();

            var canStart = Session.Players.Count >= LiarsDiceGame.MinPlayers;
            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("Start Game"))
                Client.Send(new StartGame());
            ImGui.EndDisabled();
            if (!canStart)
                ImGui.TextDisabled($"Need at least {LiarsDiceGame.MinPlayers} players — add a bot to play solo.");
        }
        else
        {
            ImGui.TextDisabled("Waiting for the host to start...");
        }

        if (ImGui.Button("Leave")) LeaveRoom();
    }

    private void DrawGame()
    {
        DrawRoster();

        ImGui.Separator();

        // Standing bid, shown as quantity × a real die face.
        if (Session.CurrentBid is { } bid)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Standing bid:");
            ImGui.SameLine();
            ImGui.TextColored(Gold, bid.Quantity.ToString());
            ImGui.SameLine(0, 4);
            ImGui.TextDisabled("×");
            ImGui.SameLine(0, 6);
            DiceRenderer.Die(bid.FaceValue, 22f);
        }
        else
        {
            ImGui.TextDisabled("No bid yet — the opening bid is free.");
        }

        if (!Session.IsSpectator)
        {
            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Your dice:");
            ImGui.SameLine(0, 8);
            if (Session.MyDice.Length == 0)
                ImGui.TextDisabled("(none)");
            else
                DiceRenderer.Hand(Session.MyDice, 30f);
        }

        ImGui.Separator();
        if (Session.IsMyTurn)
            DrawBidControls();
        else if (!Session.IsSpectator)
            ImGui.TextDisabled($"Waiting for {Session.NameOf(Session.CurrentPlayerId)}...");

        // Calling liar is open — available any time there's a standing bid you didn't make.
        if (Session.CanCallLiar)
        {
            if (ImGui.Button("Call Liar!"))
                Client.Send(new Challenge());
            if (!Session.IsMyTurn)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"(call {Session.NameOf(Session.CurrentBidderId)}'s bluff)");
            }
        }

        if (ImGui.Button(Session.IsSpectator ? "Stop watching" : "Leave")) LeaveRoom();
    }

    private static readonly Vector4 Gold = new(1f, 0.82f, 0.32f, 1f);
    private static readonly Vector4 TurnGreen = new(0.45f, 1f, 0.5f, 1f);
    private static readonly Vector4 Grey = new(0.5f, 0.5f, 0.5f, 1f);

    /// <summary>The table: each player, their hidden dice, and a marker for whose turn it is.</summary>
    private void DrawRoster()
    {
        ImGui.TextDisabled("At the table");
        foreach (var p in Session.Players)
        {
            var isCurrent = p.Id == Session.CurrentPlayerId && !p.Eliminated;
            var tag = p.Id == Session.MyId ? " (you)" : p.IsBot ? " (bot)" : "";

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(isCurrent ? TurnGreen : Grey, isCurrent ? ">>" : "  ");
            ImGui.SameLine(0, 4);

            var nameColor = p.Eliminated ? Grey : isCurrent ? TurnGreen : Vector4.One;
            ImGui.TextColored(nameColor, p.Name + tag);
            ImGui.SameLine(0, 10);

            if (p.Eliminated)
                ImGui.TextDisabled("— out");
            else if (p.DiceCount > 0)
                DiceRenderer.HiddenHand(p.DiceCount, 15f);
        }
    }

    private void DrawBidControls()
    {
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "Your turn!");

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Quantity", ref _bidQuantity);
        if (_bidQuantity < 1) _bidQuantity = 1;

        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt("Face", ref _bidFace, Bid.MinFace, Bid.MaxFace);

        var proposed = new Bid(_bidQuantity, _bidFace);
        var legal = proposed.IsValidShape &&
                    (Session.CurrentBid is not { } cur || proposed.IsHigherThan(cur.ToBid()));

        ImGui.BeginDisabled(!legal);
        if (ImGui.Button("Place Bid"))
            Client.Send(new PlaceBid(_bidQuantity, _bidFace));
        ImGui.EndDisabled();

        if (!legal)
            ImGui.TextDisabled("Bid must raise the standing bid.");
    }

    private void DrawGameOver()
    {
        var winner = Session.WinnerId is { } w ? Session.NameOf(w) : "nobody";
        ImGui.TextColored(Gold, $"{winner} wins!");
        ImGui.Spacing();
        DrawRoster();

        ImGui.Spacing();
        if (ImGui.Button("Leave Room")) LeaveRoom();
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
