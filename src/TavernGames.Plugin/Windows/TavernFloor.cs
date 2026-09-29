using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using TavernGames.Core.Games.Blackjack;
using TavernGames.Core.Games.Holdem;
using TavernGames.Core.Games.LiarsDice;
using TavernGames.Core.Games.Mia;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Games.Roulette;
using TavernGames.Core.Games.ShipCaptainCrew;
using TavernGames.Core.Platform;
using TavernGames.Plugin.Game;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// The tavern floor: what the table column shows when you are not sitting at a table. One line
/// saying what is on, the games as tiles, and a dock along the bottom for the things that are
/// not games: joining a code, your venues, your profile, setup. Opening anything takes the
/// column with a way back in its corner. Adding a game means adding a descriptor to the
/// catalog; the tile draws itself from it.
/// </summary>
internal sealed class TavernFloor
{
    private readonly record struct App(string Key, string Name, FontAwesomeIcon Icon, Func<string> Status, Action Draw, Func<bool>? Available = null, Func<string>? Unavailable = null);

    private const float IconSize = 52f;
    private const float CellWidth = 104f;
    private const float DockHeight = 82f;

    private readonly Plugin _plugin;
    private readonly GameScreen _gameScreen;
    private readonly List<App> _dock;
    private string? _open;

    public TavernFloor(Plugin plugin)
    {
        _plugin = plugin;
        _gameScreen = new GameScreen(plugin, GoHome);
        var join = new JoinApp(plugin);
        var venues = new VenuesApp(plugin, OpenGameScreen);
        var profile = new ProfileApp(plugin);
        var setup = new SetupApp(plugin);

        _dock =
        [
            new("join", "Join a code", FontAwesomeIcon.Hashtag, () => Online ? "sit down or watch" : "needs a server", join.Draw),
            new("venues", "Venues", FontAwesomeIcon.Beer, VenuesStatus, venues.Draw, () => Online, () => "Connect to a server to see your venues."),
            new("profile", "Profile", FontAwesomeIcon.User, () => plugin.ResolvePlayerName(), profile.Draw),
            new("setup", "Setup", FontAwesomeIcon.Cog, () => Ui.Pretty(plugin.Config.ServerUrl), setup.Draw),
        ];
    }

    private bool Online => _plugin.Client.State == ConnectionState.Connected && !_plugin.Client.IsLocal;

    /// <summary>What is open, or null for the floor itself.</summary>
    public string? Open => _open;

    public void GoHome() => _open = null;

    public void OpenApp(string key) => _open = key;

    /// <summary>Opens the last game's screen with a venue to host for, from the venue page.</summary>
    public void OpenGameScreen(string? hostVenueId)
    {
        var game = GameCatalog.Find(_plugin.Config.LastGameType) ?? GameCatalog.Games[0];
        _gameScreen.Open(game, hostVenueId);
        _open = "game";
    }

    /// <summary>A name for the title bar: the open thing's, or nothing on the floor.</summary>
    public string Title() => _open switch
    {
        null => "",
        "game" => _gameScreen.Game.DisplayName,
        _ => _dock.FirstOrDefault(a => a.Key == _open).Name ?? "",
    };

    public void Draw()
    {
        if (_open == "game")
        {
            DrawAppHeader(_gameScreen.Game.DisplayName, $"{_gameScreen.Game.MinPlayers}-{_gameScreen.Game.MaxPlayers} players");
            if (ImGui.BeginChild("##app", new Vector2(-1f, -1f), false, ImGuiWindowFlags.None))
                _gameScreen.Draw();
            ImGui.EndChild();
            return;
        }

        if (_open is { } key)
        {
            var app = _dock.FirstOrDefault(a => a.Key == key);
            if (app.Key is null)
            {
                _open = null;
                return;
            }

            DrawAppHeader(app.Name, app.Status());
            if (ImGui.BeginChild("##app", new Vector2(-1f, -1f), false, ImGuiWindowFlags.None))
                app.Draw();
            ImGui.EndChild();
            return;
        }

        DrawNowOn();

        if (ImGui.BeginChild("##floor", new Vector2(-1f, -DockHeight), false, ImGuiWindowFlags.None))
            DrawGames();
        ImGui.EndChild();

        DrawDock();
    }

    /// <summary>The way back, the open thing's name, and its status, on one line above it.</summary>
    private void DrawAppHeader(string name, string status)
    {
        using (Theme.PushDisplay())
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            if (ImGui.SmallButton("< TAVERN"))
                _open = null;
            ImGui.PopStyleColor();
            Ui.Tip("Back to the floor");

            ImGui.SameLine(0f, 12f);
            ImGui.TextColored(Theme.Accent, name.ToUpperInvariant());
        }

        if (status.Length > 0)
        {
            ImGui.SameLine(0f, 10f);
            ImGui.TextColored(Theme.TextFaint, Ui.Fit(status, ImGui.GetContentRegionAvail().X));
        }

        var dl = ImGui.GetWindowDrawList();
        var y = ImGui.GetItemRectMax().Y + 5f;
        var left = ImGui.GetCursorScreenPos().X;
        dl.AddLine(new Vector2(left, y), new Vector2(left + ImGui.GetContentRegionAvail().X, y), Theme.U32(Theme.Edge), 1f);
        ImGui.Dummy(new Vector2(0f, 8f));
    }

    /// <summary>One line: nothing is on, and the quickest way to get something on.</summary>
    private void DrawNowOn()
    {
        var config = _plugin.Config;
        ImGui.AlignTextToFramePadding();
        Theme.Displayed(Theme.TextFaint, "NOTHING ON");
        ImGui.SameLine(0f, 12f);

        var last = GameCatalog.Find(config.LastGameType);
        if (last is not null && config.LocalPlayed.GetValueOrDefault(last.Type) > 0)
        {
            var bots = config.LastBots == 1 ? "1 bot" : $"{config.LastBots} bots";
            var buttonWidth = ImGui.CalcTextSize("Play again").X + 16f;
            ImGui.TextColored(Theme.TextDim, Ui.Fit($"Last: {last.DisplayName} against {bots}", ImGui.GetContentRegionAvail().X - buttonWidth - 12f));
            ImGui.SameLine(0f, 10f);
            if (ImGui.SmallButton("Play again"))
                _gameScreen.PlayAgainstBots(last, config.LastBots);
        }
        else
        {
            ImGui.TextColored(Theme.TextDim, "Pick a game, or join a friend's table with a code.");
        }

        ImGui.Dummy(new Vector2(0f, 6f));
    }

    /// <summary>The games as tiles, the width deciding how many to a row.</summary>
    private void DrawGames()
    {
        var available = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)(available / CellWidth));
        var cell = available / columns;
        var games = GameCatalog.Games;

        for (var i = 0; i < games.Count; i++)
        {
            if (i % columns != 0)
                ImGui.SameLine(0f, 0f);
            DrawGameTile(games[i], cell);
        }
    }

    /// <summary>One game: a rounded square with its icon drawn in it, the name below, the blurb as a tooltip.</summary>
    private void DrawGameTile(GameDescriptor game, float cellWidth)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var height = IconSize + 6f + ImGui.GetTextLineHeight() + 12f;

        if (ImGui.InvisibleButton($"##tile{game.Type}", new Vector2(cellWidth, height)))
        {
            _gameScreen.Open(game, null);
            _open = "game";
        }

        var hovered = ImGui.IsItemHovered();
        Ui.Tip($"{game.Blurb} ({game.MinPlayers}-{game.MaxPlayers} players)");

        var p0 = origin + new Vector2((cellWidth - IconSize) / 2f, 4f);
        var p1 = p0 + new Vector2(IconSize, IconSize);
        dl.AddRectFilled(p0, p1, Theme.U32(Theme.WithAlpha(Theme.Accent, hovered ? 0.30f : 0.16f)), IconSize * 0.27f);
        dl.AddRect(p0, p1, Theme.U32(Theme.WithAlpha(Theme.Accent, hovered ? 0.9f : 0.45f)), IconSize * 0.27f, ImDrawFlags.RoundCornersAll, 1f);
        DrawGameIcon(dl, game.Type, p0, IconSize, Theme.U32(Theme.Accent));

        var label = Ui.Fit(game.DisplayName, cellWidth - 8f);
        var labelSize = ImGui.CalcTextSize(label);
        dl.AddText(origin + new Vector2((cellWidth - labelSize.X) / 2f, IconSize + 10f), Theme.U32(hovered ? Theme.Text : Theme.TextDim), label);
    }

    /// <summary>
    /// A line icon per game, drawn rather than fetched: a cup, a die, two dice, two cards, a
    /// chip. A game the floor doesn't know gets a die.
    /// </summary>
    private static void DrawGameIcon(ImDrawListPtr dl, string type, Vector2 p0, float size, uint col)
    {
        var c = p0 + new Vector2(size / 2f, size / 2f);
        var s = size / 24f; // the icons are drawn on a 24-unit grid
        const float t = 1.8f;

        switch (type)
        {
            case LiarsDiceModule.Type:
                dl.AddQuad(p0 + new Vector2(5f * s, 4f * s), p0 + new Vector2(19f * s, 4f * s), p0 + new Vector2(17f * s, 20f * s), p0 + new Vector2(7f * s, 20f * s), col, t);
                dl.AddLine(p0 + new Vector2(6.3f * s, 9f * s), p0 + new Vector2(17.7f * s, 9f * s), col, t);
                break;

            case MiaModule.Type:
                dl.AddRect(p0 + new Vector2(2.5f * s, 9.5f * s), p0 + new Vector2(12.5f * s, 19.5f * s), col, 2f * s, ImDrawFlags.RoundCornersAll, t);
                dl.AddRect(p0 + new Vector2(11.5f * s, 4.5f * s), p0 + new Vector2(21.5f * s, 14.5f * s), col, 2f * s, ImDrawFlags.RoundCornersAll, t);
                dl.AddCircleFilled(p0 + new Vector2(7.5f * s, 14.5f * s), 1.2f * s, col, 10);
                dl.AddCircleFilled(p0 + new Vector2(14.5f * s, 7.5f * s), 1.2f * s, col, 10);
                dl.AddCircleFilled(p0 + new Vector2(18.5f * s, 11.5f * s), 1.2f * s, col, 10);
                break;

            case BlackjackModule.Type:
                dl.AddRect(p0 + new Vector2(9f * s, 4f * s), p0 + new Vector2(20f * s, 20f * s), col, 2f * s, ImDrawFlags.RoundCornersAll, t);
                dl.AddRectFilled(p0 + new Vector2(4f * s, 5f * s), p0 + new Vector2(15f * s, 21f * s), Theme.U32(Theme.Shell), 2f * s);
                dl.AddRect(p0 + new Vector2(4f * s, 5f * s), p0 + new Vector2(15f * s, 21f * s), col, 2f * s, ImDrawFlags.RoundCornersAll, t);
                break;

            case HoldemModule.Type:
                dl.AddCircle(c, 9f * s, col, 24, t);
                dl.AddCircle(c, 4.5f * s, col, 16, t);
                dl.AddLine(c + new Vector2(0f, -9f * s), c + new Vector2(0f, -6f * s), col, t);
                dl.AddLine(c + new Vector2(0f, 9f * s), c + new Vector2(0f, 6f * s), col, t);
                dl.AddLine(c + new Vector2(-9f * s, 0f), c + new Vector2(-6f * s, 0f), col, t);
                dl.AddLine(c + new Vector2(9f * s, 0f), c + new Vector2(6f * s, 0f), col, t);
                break;

            case RouletteModule.Type:
                dl.AddCircle(c, 9.5f * s, col, 32, t);
                dl.AddCircle(c, 3.5f * s, col, 16, t);
                for (var k = 0; k < 8; k++)
                {
                    var a = k * MathF.PI / 4f;
                    var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                    dl.AddLine(c + d * 3.5f * s, c + d * 9.5f * s, col, t * 0.8f);
                }
                dl.AddCircleFilled(c + new Vector2(0f, -6.8f * s), 1.6f * s, col, 10);
                break;

            case ShipCaptainCrewModule.Type:
                // A hull, a mast and a sail.
                dl.AddQuad(p0 + new Vector2(3f * s, 15f * s), p0 + new Vector2(21f * s, 15f * s), p0 + new Vector2(18f * s, 20f * s), p0 + new Vector2(6f * s, 20f * s), col, t);
                dl.AddLine(p0 + new Vector2(12f * s, 3.5f * s), p0 + new Vector2(12f * s, 15f * s), col, t);
                dl.AddTriangle(p0 + new Vector2(12.6f * s, 4.5f * s), p0 + new Vector2(19f * s, 12.5f * s), p0 + new Vector2(12.6f * s, 12.5f * s), col, t);
                break;

            case PigModule.Type:
            default:
                dl.AddRect(p0 + new Vector2(4f * s, 4f * s), p0 + new Vector2(20f * s, 20f * s), col, 3f * s, ImDrawFlags.RoundCornersAll, t);
                dl.AddCircleFilled(c, 1.7f * s, col, 12);
                break;
        }
    }

    /// <summary>The dock: joining, venues, profile and setup, on a shelf of their own at the foot.</summary>
    private void DrawDock()
    {
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var end = start + new Vector2(width, DockHeight - 4f);
        dl.AddRectFilled(start, end, Theme.U32(Theme.Glass), 14f);
        dl.AddRect(start, end, Theme.U32(Theme.GlassEdge), 14f, ImDrawFlags.RoundCornersAll, 1f);

        var cell = Math.Min(130f, (width - 24f) / _dock.Count);
        var inset = (width - cell * _dock.Count) / 2f;
        ImGui.SetCursorScreenPos(start + new Vector2(inset, 6f));
        for (var i = 0; i < _dock.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0f, 0f);
            DrawDockItem(_dock[i], cell);
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, DockHeight - 4f));
    }

    private void DrawDockItem(App app, float cellWidth)
    {
        const float iconSize = 44f;
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var height = iconSize + 6f + ImGui.GetTextLineHeight() + 4f;
        var available = app.Available?.Invoke() ?? true;

        if (ImGui.InvisibleButton($"##dock{app.Key}", new Vector2(cellWidth, height)) && available)
            _open = app.Key;

        var hovered = ImGui.IsItemHovered();
        var status = available ? app.Status() : app.Unavailable?.Invoke() ?? "";
        if (hovered && status.Length > 0)
            ImGui.SetTooltip(status);

        var p0 = origin + new Vector2((cellWidth - iconSize) / 2f, 2f);
        var p1 = p0 + new Vector2(iconSize, iconSize);
        var plain = app.Key is "profile" or "setup" || !available;
        var tint = plain ? Theme.Glass : Theme.WithAlpha(Theme.Accent, hovered ? 0.30f : 0.16f);
        var edge = plain ? Theme.GlassEdge : Theme.WithAlpha(Theme.Accent, hovered ? 0.9f : 0.45f);
        var ink = !available ? Theme.TextFaint : plain ? Theme.TextDim : Theme.Accent;
        dl.AddRectFilled(p0, p1, Theme.U32(tint), iconSize * 0.27f);
        dl.AddRect(p0, p1, Theme.U32(edge), iconSize * 0.27f, ImDrawFlags.RoundCornersAll, 1f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var icon = app.Icon.ToIconString();
            var size = ImGui.CalcTextSize(icon);
            dl.AddText(p0 + (new Vector2(iconSize, iconSize) - size) / 2f, Theme.U32(ink), icon);
        }

        var label = Ui.Fit(app.Name, cellWidth - 8f);
        var labelSize = ImGui.CalcTextSize(label);
        dl.AddText(origin + new Vector2((cellWidth - labelSize.X) / 2f, iconSize + 6f), Theme.U32(!available ? Theme.TextFaint : hovered ? Theme.Text : Theme.TextDim), label);
    }

    private string VenuesStatus()
    {
        var count = _plugin.Account.Venues.Count;
        return count == 0 ? "join one with a code" : count == 1 ? "1 venue" : $"{count} venues";
    }
}
