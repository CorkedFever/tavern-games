using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TavernGames.Core;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// The window: your seat on the left and the table beside it. The seat is always there; the
/// table shows the tavern floor until you sit down, then the felt. Folding the window puts the
/// table away and leaves the seat alone in a corner of the screen, still playable.
/// <para>
/// The whole thing is drawn by hand on a near-black shell, Aetherstream's way, so the two read
/// as siblings on the same screen. The style is pushed in PreDraw and popped in PostDraw, and
/// every section is guarded so a bug in one game can't take the window down.
/// </para>
/// </summary>
public sealed class MainWindow : Window
{
    private const float TitleBarHeight = 34f;
    private const float ColumnGap = 12f;
    private const float LogHeight = 96f;

    private readonly Plugin _plugin;
    private readonly SeatColumn _seat;
    private readonly TavernFloor _floor;

    private Vector2 _unfoldedSize = new(760f, 600f);
    private Vector2? _sizeToRestore;
    private GamePhase _lastPhase = GamePhase.Lobby;
    private string _lastDrawError = "";

    public MainWindow(Plugin plugin) : base("Tavern Games###TavernGamesMain")
    {
        _plugin = plugin;
        _seat = new SeatColumn(plugin, LeaveRoom, Guarded);
        _floor = new TavernFloor(plugin);

        Size = _unfoldedSize;
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    private Game.GameClient Client => _plugin.Client;
    private Game.GameSession Session => _plugin.Session;
    private bool Folded => _plugin.Config.WindowFolded;

    /// <summary>Shows something from the dock (setup, say), unfolding and opening the window if need be.</summary>
    public void OpenApp(string key)
    {
        _floor.OpenApp(key);
        if (Folded)
            ToggleFold();
        IsOpen = true;
    }

    public override void PreDraw()
    {
        var folded = Folded;

        // Folded, the window shrinks to the seat rather than leaving a dark slab beside it.
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | (folded ? ImGuiWindowFlags.AlwaysAutoResize : ImGuiWindowFlags.None);

        SizeConstraints = folded
            ? new WindowSizeConstraints { MinimumSize = new Vector2(SeatColumn.BodyWidth + 24f, TitleBarHeight + 24f), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) }
            : new WindowSizeConstraints { MinimumSize = new Vector2(SeatColumn.BodyWidth + ColumnGap + 420f, 480f), MaximumSize = new Vector2(1600f, 1600f) };

        if (_sizeToRestore is { } restore)
        {
            Size = restore;
            SizeCondition = ImGuiCond.Always;
            _sizeToRestore = null;
        }
        else
        {
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        Theme.PushShell();
    }

    public override void PostDraw() => Theme.PopShell();

    /// <summary>
    /// The style pushed in PreDraw is popped in PostDraw, so an exception escaping here would
    /// leave the stack unbalanced and restyle every other plugin's window for the rest of the
    /// frame. Nothing in a draw call is worth that.
    /// </summary>
    public override void Draw()
    {
        try
        {
            DrawContents();
        }
        catch (Exception ex)
        {
            LogOnce("window", ex);
        }
    }

    /// <summary>
    /// Runs one section of the UI so that a bug in it can't take the window down. Each section
    /// is guarded from inside whatever ImGui scope contains it, so when it throws the enclosing
    /// End call still runs and ImGui's stack stays balanced. The same error is logged once.
    /// </summary>
    private void Guarded(string section, Action draw)
    {
        try
        {
            draw();
        }
        catch (Exception ex)
        {
            LogOnce(section, ex);
            ImGui.TextColored(Theme.Bad, "A UI error occurred here. See /xllog for details.");
        }
    }

    private void LogOnce(string section, Exception ex)
    {
        var signature = $"{section}: {ex.GetType().Name}: {ex.Message}";
        if (signature == _lastDrawError)
            return;
        _lastDrawError = signature;
        Plugin.Log.Error(ex, "Tavern Games: UI error while drawing {Section}.", section);
    }

    private void DrawContents()
    {
        Fx.Enabled = _plugin.Config.Effects;
        Sound.Enabled = _plugin.Config.Sounds;
        Fx.Tick();

        Theme.WindowFrame();
        TrackPhase();
        DrawTitleBar();

        if (Folded)
        {
            Guarded("seat", () => _seat.Draw(stretch: false));
            return;
        }

        // The seat in its own column, so the table beside it scrolls without moving it.
        if (ImGui.BeginChild("##seat", new Vector2(SeatColumn.BodyWidth, -1f), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            Guarded("seat", () => _seat.Draw(stretch: true));
        ImGui.EndChild();

        ImGui.SameLine(0f, ColumnGap);

        // The seat sizes everything to its column; the table sizes to the window. If the seat
        // threw before it could say so, this puts it right rather than squeezing the table.
        Ui.ColumnWidth = null;

        if (ImGui.BeginChild("##table", new Vector2(-1f, -1f), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (Session.InRoom)
                DrawRoom();
            else
                Guarded("floor", _floor.Draw);
        }
        ImGui.EndChild();
    }

    /// <summary>
    /// Watches the table change stage: folds to the seat the moment the cards go round, when
    /// asked to, and stages the end of the game, which no single game message announces.
    /// </summary>
    private void TrackPhase()
    {
        var phase = Session.InRoom ? Session.Phase : GamePhase.Lobby;
        if (phase == _lastPhase)
            return;

        if (phase == GamePhase.Playing)
        {
            // Whatever the last game left playing (confetti, mostly) has no place at a new one.
            Fx.Reset();
            if (_plugin.Config.FoldOnDeal && !Folded && !Session.IsSpectator)
                ToggleFold();
        }
        else if (phase == GamePhase.GameOver && _lastPhase == GamePhase.Playing)
        {
            StageGameOver();
        }

        _lastPhase = phase;
    }

    /// <summary>Victory, defeat, or somebody else's win: the one moment every game shares.</summary>
    private void StageGameOver()
    {
        var winner = Session.WinnerId is { } w ? Session.NameOf(w) : "Nobody";
        if (Session.IsSpectator || Session.MyId.Length == 0)
        {
            Fx.StampFelt($"{winner} wins", Theme.Accent, 2.5);
            Fx.Confetti(3.5);
            if (Session.WinnerId is { } id) Fx.Glow(id, Theme.Good, 3.0);
        }
        else if (Session.WinnerId == Session.MyId)
        {
            Fx.StampFelt("Victory", Theme.Good, 3.0);
            Fx.Confetti(4.5);
            Fx.Glow(Session.MyId, Theme.Good, 4.0);
            Sound.Victory();
        }
        else
        {
            Fx.StampFelt("Defeat", Theme.Bad, 2.5);
            Fx.Vignette(Theme.Bad);
            if (Session.WinnerId is { } id) Fx.Glow(id, Theme.Good, 3.0);
            Sound.Defeat();
        }
    }

    // -- the title bar -----------------------------------------------------------------------

    /// <summary>
    /// The nameplate, what is open, and the fold and close buttons, drawn by hand because the
    /// whole window is drawn by hand and Dalamud's title bar would sit on it like a sticker.
    /// </summary>
    private void DrawTitleBar()
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Folded ? SeatColumn.BodyWidth : ImGui.GetContentRegionAvail().X;

        dl.AddRectFilled(origin, origin + new Vector2(width, TitleBarHeight), Theme.U32(Theme.TitleBarFill), 6f);

        using (Theme.PushDisplay())
        {
            const string caption = "TAVERN GAMES";
            var captionSize = ImGui.CalcTextSize(caption);
            var closeWidth = ImGui.CalcTextSize("×").X + 16f;
            var foldWidth = ImGui.CalcTextSize("_").X + 16f;
            var buttonsWidth = closeWidth + foldWidth;
            var buttonHeight = MathF.Max(captionSize.Y, 16f);

            // The drag area stops short of the buttons: ImGui gives a click to whichever item
            // claimed the spot first, so a bar spanning the whole width would make the close
            // button impossible to press.
            ImGui.InvisibleButton("##titlebar", new Vector2(MathF.Max(1f, width - buttonsWidth), TitleBarHeight));
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);

            ImGui.SetCursorScreenPos(origin + new Vector2(12f, (TitleBarHeight - captionSize.Y) / 2f));
            ImGui.TextColored(Theme.Text, caption);

            // What is open, right-aligned against the buttons: the game and the room, or the app.
            if (!Folded)
            {
                var title = Title();
                if (title.Length > 0)
                {
                    title = Ui.Ellipsis(title, 34).ToUpperInvariant();
                    var titleSize = ImGui.CalcTextSize(title);
                    ImGui.SetCursorScreenPos(origin + new Vector2(width - titleSize.X - buttonsWidth - 8f, (TitleBarHeight - titleSize.Y) / 2f));
                    ImGui.TextColored(Theme.TextDim, title);
                }
            }

            ImGui.SetCursorScreenPos(origin + new Vector2(width - buttonsWidth, (TitleBarHeight - buttonHeight) / 2f));
            if (ImGui.InvisibleButton("##fold", new Vector2(foldWidth, buttonHeight)))
                ToggleFold();
            var foldHovered = ImGui.IsItemHovered();
            if (foldHovered)
                ImGui.SetTooltip(Folded ? "Unfold the table" : "Fold down to your seat; the game keeps going");

            ImGui.SetCursorScreenPos(origin + new Vector2(width - buttonsWidth + 6f, (TitleBarHeight - captionSize.Y) / 2f - (Folded ? 0f : 4f)));
            ImGui.TextColored(foldHovered ? Theme.Text : Theme.TextFaint, Folded ? "^" : "_");

            ImGui.SetCursorScreenPos(origin + new Vector2(width - closeWidth, (TitleBarHeight - buttonHeight) / 2f));
            if (ImGui.InvisibleButton("##close", new Vector2(closeWidth, buttonHeight)))
                IsOpen = false;
            var closeHovered = ImGui.IsItemHovered();
            ImGui.SetCursorScreenPos(origin + new Vector2(width - closeWidth + 6f, (TitleBarHeight - captionSize.Y) / 2f));
            ImGui.TextColored(closeHovered ? Theme.Bad : Theme.TextFaint, "×");
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, TitleBarHeight + 8f));
    }

    private string Title()
    {
        if (!Session.InRoom)
            return _floor.Title();
        var game = GameCatalog.Find(Session.GameType)?.DisplayName ?? Session.GameType;
        return $"{game} · {Session.RoomCode}";
    }

    private void ToggleFold()
    {
        // Folding lets the window shrink to the seat, which is the size ImGui would then
        // remember; the size before folding is kept and put back on unfolding.
        if (!Folded)
            _unfoldedSize = ImGui.GetWindowSize();
        else
            _sizeToRestore = _unfoldedSize;

        _plugin.Config.WindowFolded = !Folded;
        _plugin.Config.Save();
    }

    // -- the table ---------------------------------------------------------------------------

    private void DrawRoom()
    {
        DrawRoomHeader();

        var feltHeight = ImGui.GetContentRegionAvail().Y - LogHeight - ImGui.GetStyle().ItemSpacing.Y;
        if (Felt.Begin("##felt", feltHeight))
        {
            switch (Session.Phase)
            {
                case GamePhase.Lobby:
                    Guarded("lobby", DrawLobbyFelt);
                    break;
                case GamePhase.Playing when Session.ActiveGame is { } game:
                    Guarded(game.GameType + " felt", () => game.DrawFelt(Session));
                    break;
                case GamePhase.Playing:
                    Theme.Marquee("Unknown game", Theme.Bad, $"This plugin version doesn't know '{Session.GameType}'. Update to play it.");
                    break;
                case GamePhase.GameOver:
                    Guarded("game over", DrawGameOverFelt);
                    break;
            }
        }
        Felt.End();

        DrawLog();
    }

    /// <summary>Which stage the table is at, what is being played and for whom.</summary>
    private void DrawRoomHeader()
    {
        var game = GameCatalog.Find(Session.GameType);
        var name = game?.DisplayName ?? Session.GameType;
        var (word, description) = Session.Phase switch
        {
            GamePhase.Lobby => ("Lobby", "Share the code so friends can sit down or watch."),
            GamePhase.Playing => ("At the table", Session.VenueName is { } venue ? $"{name} · {venue}" : name),
            _ => ("Game over", Session.WinnerId is { } w ? $"{Session.NameOf(w)} takes the table." : "Nobody won."),
        };

        var right = Session.Phase == GamePhase.Lobby
            ? $"{Session.Players.Count}/{game?.MaxPlayers ?? 6} seated"
            : Session.IsSpectator ? "watching" : "";
        var rightWidth = right.Length > 0 ? ImGui.CalcTextSize(right).X + 12f : 0f;

        ImGui.AlignTextToFramePadding();
        Theme.Displayed(Theme.Accent, word.ToUpperInvariant());
        ImGui.SameLine(0f, 12f);
        ImGui.TextColored(Theme.TextDim, Ui.Fit(description, ImGui.GetContentRegionAvail().X - rightWidth));
        if (right.Length > 0)
        {
            ImGui.SameLine();
            Ui.RightAlignedText(right, Theme.TextFaint);
        }
        ImGui.Dummy(new Vector2(0f, 2f));
    }

    /// <summary>The lobby on the felt: the code, big, and a plate for every seat, filled or waiting.</summary>
    private void DrawLobbyFelt()
    {
        var game = GameCatalog.Find(Session.GameType);
        var max = game?.MaxPlayers ?? 6;

        ImGui.Dummy(new Vector2(0f, 4f));
        Theme.Marquee(Session.RoomCode, Theme.Accent);

        if (!Client.IsLocal)
        {
            var hint = "They enter it under Join a code, or find the table on the venue page.";
            var copyWidth = ImGui.CalcTextSize("Copy").X + 16f;
            Ui.CenterNext(copyWidth + 8f + ImGui.CalcTextSize(hint).X);
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText(Session.RoomCode);
            ImGui.SameLine(0f, 8f);
            ImGui.TextColored(Theme.TextFaint, hint);
        }

        ImGui.Dummy(new Vector2(0f, 10f));

        var isHost = Session.IsHost;
        Felt.Plates(
            Session,
            tally: _ => ImGui.TextColored(Theme.TextFaint, "ready"),
            nameLine: isHost ? RemoveBotControl : null,
            emptySeats: Math.Max(0, max - Session.Players.Count),
            seatBot: isHost ? () => Client.Send(new AddBot()) : null);
    }

    private void RemoveBotControl(PlayerPublic p)
    {
        if (!p.IsBot)
            return;
        ImGui.SameLine();
        if (ImGui.SmallButton("remove"))
            Client.Send(new RemoveBot(p.Id));
        Ui.Tip("Send this bot away.");
    }

    private void DrawGameOverFelt()
    {
        var winner = Session.WinnerId is { } w ? Session.NameOf(w) : "Nobody";
        ImGui.Dummy(new Vector2(0f, 4f));
        Theme.Marquee($"{winner} wins", Theme.Accent, Session.WinnerId == Session.MyId ? "The table is yours." : "Well played, all.");
        ImGui.Dummy(new Vector2(0f, 10f));
        Felt.Plates(Session, Session.ActiveGame is { } game ? game.DrawSeatTally : null);
    }

    /// <summary>The log under the felt: what happened, newest at the bottom and brightest.</summary>
    private void DrawLog()
    {
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, LogHeight);
        dl.AddRectFilled(start, start + size, Theme.U32(Theme.Glass), Theme.PanelRounding);
        dl.AddRect(start, start + size, Theme.U32(Theme.GlassEdge), Theme.PanelRounding, ImDrawFlags.RoundCornersAll, 1f);

        ImGui.SetCursorScreenPos(start + new Vector2(8f, 4f));
        if (ImGui.BeginChild("##log", size - new Vector2(16f, 8f), false, ImGuiWindowFlags.None))
        {
            var log = Session.Log;
            for (var i = 0; i < log.Count; i++)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, i == log.Count - 1 ? Theme.Text : Theme.TextFaint);
                ImGui.TextWrapped(log[i]);
                ImGui.PopStyleColor();
            }
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(size);
    }

    private void LeaveRoom()
    {
        Client.Send(new LeaveRoom());
        Session.Reset();
        Fx.Reset();
        _floor.GoHome();
    }
}
