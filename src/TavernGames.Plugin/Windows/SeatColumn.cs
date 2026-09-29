using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// Your seat: the left column, and the whole window once it is folded. A display strip that
/// says what the table wants of you, your name, what only you can see (your dice, your cards),
/// the keys for what you can do, and a line at the foot saying where you are sitting.
/// <para>
/// It sits outside the floor and the felt deliberately: bidding, folding or calling liar should
/// never mean looking away from the table, and folded down to this column alone the game can be
/// played from a corner of the screen while the roleplay carries on around it.
/// </para>
/// </summary>
internal sealed class SeatColumn(Plugin plugin, Action leave, Action<string, Action> guarded)
{
    public const float Width = 176f;

    private const float Pad = 8f;

    /// <summary>The slab around the content: what the folded window and the left column measure.</summary>
    public const float BodyWidth = Width + Pad * 2f;

    private const float StripHeight = 30f;

    /// <summary>What the strip said last frame, so a change can light the seat and ring a chime.</summary>
    private string _lastWord = "";

    /// <summary>
    /// Draws the seat. Stretched, the slab runs to the foot of the space it is given and the
    /// foot line sits at the bottom; folded, the window fits the slab instead.
    /// </summary>
    public void Draw(bool stretch)
    {
        var fullHeight = stretch ? ImGui.GetContentRegionAvail().Y : 0f;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();

        // The slab is drawn under the content once its height is known: two channels, the
        // content on top, the slab filled in afterwards on the one beneath.
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(Pad, Pad));
        ImGui.BeginGroup();

        // Everything in the column is sized to this width, never to the window: a folded window
        // sizes itself to its content, and content sized to the window would chase it forever.
        Ui.ColumnLeft = ImGui.GetCursorPosX();
        Ui.ColumnWidth = Width;

        DrawDisplay();
        DrawNamePlate();
        DrawBody();

        if (stretch)
        {
            var slack = (start.Y + fullHeight - Pad - FootHeight()) - ImGui.GetCursorScreenPos().Y;
            if (slack > 0f)
                ImGui.Dummy(new Vector2(0f, slack));
        }

        DrawFoot();

        Ui.ColumnWidth = null;
        ImGui.EndGroup();

        var end = new Vector2(start.X + BodyWidth, MathF.Max(ImGui.GetItemRectMax().Y + Pad, start.Y + fullHeight));
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(start, end, Theme.U32(Theme.Glass), 16f);
        dl.AddRect(start, end, Theme.U32(Theme.GlassEdge), 16f, ImDrawFlags.RoundCornersAll, 1f);

        // The whole seat lights up when the table turns to you, or on you: a folded seat in the
        // corner of the screen has nothing else to catch the eye with.
        var amount = Fx.GlowAmount("seat", out var glow);
        if (amount > 0f)
        {
            dl.AddRect(start - new Vector2(1f, 1f), end + new Vector2(1f, 1f), Theme.U32(Theme.WithAlpha(glow, amount)), 17f, ImDrawFlags.RoundCornersAll, 2.5f);
            dl.AddRect(start - new Vector2(4f, 4f), end + new Vector2(4f, 4f), Theme.U32(Theme.WithAlpha(glow, 0.35f * amount)), 20f, ImDrawFlags.RoundCornersAll, 6f);
        }

        dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(end - start);
    }

    // -- the display strip -------------------------------------------------------------------

    /// <summary>
    /// The strip at the top: one word in the display face saying what the table wants of you,
    /// and a little LED. Drawn like a lit sign on the seat rather than as text on a window.
    /// </summary>
    private void DrawDisplay()
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(Width, StripHeight);

        dl.AddRectFilled(origin, origin + size, Theme.U32(Theme.StripFill), 6f);
        dl.AddRect(origin, origin + size, Theme.U32(Theme.GlassEdge), 6f, ImDrawFlags.RoundCornersAll, 1f);
        dl.AddCircleFilled(origin + new Vector2(10f, size.Y / 2f), 3f, Theme.U32(Led()), 12);

        var (word, colour) = Display();

        // A change of word is a moment: the seat lights up, and the game's own chime says so.
        var yours = word is "YOUR TURN" or "PLACE YOUR BET";
        if (word != _lastWord)
        {
            _lastWord = word;
            if (yours)
            {
                Fx.Glow("seat", Theme.Accent, 0.9);
                Sound.YourTurn();
            }
            else if (colour == Theme.Bad)
            {
                Fx.Glow("seat", Theme.Bad, 0.9);
            }
            else if (colour == Theme.Good)
            {
                Fx.Glow("seat", Theme.Good, 1.2);
            }
        }

        // Your turn pulses, slowly, so a folded seat in the corner of the screen still catches the eye.
        if (yours)
            colour = Theme.WithAlpha(colour, 0.7f + 0.3f * MathF.Sin((float)ImGui.GetTime() * 4f));

        using (Theme.PushDisplay())
        {
            var text = Ui.Fit(word.ToUpperInvariant(), Width - 28f);
            var textSize = ImGui.CalcTextSize(text);
            ImGui.SetCursorScreenPos(origin + new Vector2(22f, (size.Y - textSize.Y) / 2f));
            ImGui.TextColored(colour, text);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    private (string Word, Vector4 Colour) Display()
    {
        var session = plugin.Session;
        var client = plugin.Client;

        if (client.State == ConnectionState.Connecting)
            return ("CONNECTING", Theme.Accent);
        if (!session.InRoom)
            return ("NO TABLE", Theme.TextFaint);
        if (session.IsSpectator)
            return (session.Phase == GamePhase.GameOver ? "GAME OVER" : "WATCHING", Theme.TextDim);

        switch (session.Phase)
        {
            case GamePhase.Lobby:
                return ($"ROOM {session.RoomCode}", Theme.Accent);
            case GamePhase.Playing when session.ActiveGame is { } game:
                var state = game.Display(session);
                return (state.Word, state.Colour);
            case GamePhase.Playing:
                return ("PLAYING", Theme.Accent);
            default:
                return session.WinnerId == session.MyId ? ("YOU WIN", Theme.Good) : ("GAME OVER", Theme.TextDim);
        }
    }

    /// <summary>The LED: off with nothing on, amber while connecting, green at a table, red when the server went away.</summary>
    private Vector4 Led()
    {
        var client = plugin.Client;
        if (client.State == ConnectionState.Connecting) return Theme.Accent;
        if (client.State == ConnectionState.Connected) return Theme.Good;
        return client.LastError is not null ? Theme.Bad : Theme.Edge;
    }

    // -- the name plate ----------------------------------------------------------------------

    private void DrawNamePlate()
    {
        var session = plugin.Session;
        var name = plugin.ResolvePlayerName();
        var tag = session.InRoom && session.IsHost ? " host" : "";
        var tagWidth = tag.Length > 0 ? ImGui.CalcTextSize(tag).X : 0f;

        ImGui.Dummy(new Vector2(0f, 2f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4f);
        ImGui.TextColored(Theme.Text, Ui.Fit(name, Width - 8f - tagWidth));
        if (tag.Length > 0)
        {
            ImGui.SameLine(0f, 0f);
            ImGui.TextColored(Theme.TextFaint, tag);
        }

        var tagline = plugin.Config.Tagline;
        if (tagline.Length > 0)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4f);
            ImGui.TextColored(Theme.TextFaint, Ui.Fit(tagline, Width - 8f));
        }
    }

    // -- the body ----------------------------------------------------------------------------

    private void DrawBody()
    {
        var session = plugin.Session;

        if (!session.InRoom)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("Pick a game on the floor, or join a friend's table with their code. Your seat fills in once you sit down: your dice or cards here, the table beside it.");
            return;
        }

        if (session.IsSpectator)
        {
            ImGui.Dummy(new Vector2(0f, 4f));
            Ui.Hint("You're on the rail. You see what the table sees, and nothing more.");
            return;
        }

        switch (session.Phase)
        {
            case GamePhase.Lobby:
                DrawLobbyKeys();
                break;

            case GamePhase.Playing when session.ActiveGame is { } game:
                guarded(game.GameType + " seat", () => game.DrawSeat(session, plugin.Client.Send));
                break;

            case GamePhase.Playing:
                Ui.Hint($"This plugin version doesn't know the game '{session.GameType}'. Update to play it.");
                break;

            case GamePhase.GameOver:
                ImGui.Dummy(new Vector2(0f, 4f));
                var winner = session.WinnerId is { } w ? session.NameOf(w) : "Nobody";
                Ui.Hint(session.WinnerId == session.MyId
                    ? "The table is yours. Leave whenever you like; your record has it."
                    : $"{winner} takes the table. Leave to head back to the floor.");
                break;
        }
    }

    private void DrawLobbyKeys()
    {
        var session = plugin.Session;
        var game = GameCatalog.Find(session.GameType);
        var min = game?.MinPlayers ?? 2;
        var max = game?.MaxPlayers ?? 6;
        var count = session.Players.Count;

        Theme.Heading("The table");

        if (!session.IsHost)
        {
            Ui.Hint($"{count} of {max} seated. Waiting for the host to start; share the code meanwhile.");
            return;
        }

        var canStart = count >= min;
        if (Ui.Key("Start", 0f, canStart ? KeyStyle.Lit : KeyStyle.Plain, canStart,
                canStart ? "Deal everyone in." : $"Needs {min} players. Seat a bot to play right away."))
            plugin.Client.Send(new StartGame());

        var full = count >= max;
        if (Ui.Key("+ Bot", 0f, KeyStyle.Plain, !full, full ? "Every seat is taken." : "Fill an empty seat with a bot."))
            plugin.Client.Send(new AddBot());

        Ui.Hint(canStart ? $"{count} of {max} seated. Start whenever." : $"{count} of {max} seated. Needs {min} to start.");
    }

    // -- the foot ----------------------------------------------------------------------------

    private float FootHeight()
    {
        var line = ImGui.GetTextLineHeightWithSpacing();
        var lines = plugin.Session.InRoom ? 3 : plugin.Client.LastError is null ? 1 : 2;
        return 10f + line * lines + Ui.KeyHeight + ImGui.GetStyle().ItemSpacing.Y;
    }

    /// <summary>Where you are sitting and the last thing that happened, so the folded seat still says.</summary>
    private void DrawFoot()
    {
        var session = plugin.Session;
        var client = plugin.Client;
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        dl.AddLine(origin, origin + new Vector2(Width, 0f), Theme.U32(Theme.Edge), 1f);
        ImGui.Dummy(new Vector2(0f, 4f));

        if (session.InRoom)
        {
            var game = GameCatalog.Find(session.GameType)?.DisplayName ?? session.GameType;
            ImGui.TextColored(Theme.TextDim, Ui.Fit($"{game} · {session.RoomCode}", Width));
            ImGui.TextColored(Theme.TextFaint, Ui.Fit(session.VenueName is { } venue ? $"Hosted by {venue}" : client.IsLocal ? "Against the bots, no server" : "A private table", Width));
            ImGui.TextColored(Theme.TextFaint, Ui.Fit(session.Log.Count > 0 ? session.Log[^1] : "", Width));

            if (Ui.Key(session.IsSpectator ? "Stop watching" : "Leave", 0f, KeyStyle.Faint, true,
                    session.IsSpectator ? "Back to the floor." : client.IsLocal ? "Walk away from the bots." : "Walk away from the table. The others play on."))
                leave();
            return;
        }

        var connected = client.State == ConnectionState.Connected && !client.IsLocal;
        var (led, status) = client.State switch
        {
            ConnectionState.Connecting => (Theme.Accent, "Connecting..."),
            ConnectionState.Connected when connected => (Theme.Good, $"Connected as {plugin.Account.Profile?.DisplayName ?? "a guest"}"),
            _ => (Theme.Edge, "Offline · bots only"),
        };

        Ui.Dot(led);
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(Theme.TextDim, Ui.Fit(status, Width - 20f));

        if (client.LastError is { } error && client.State == ConnectionState.Disconnected)
        {
            ImGui.TextColored(Theme.Bad, Ui.Fit(error, Width));
            Ui.Tip(error);
        }

        if (connected)
        {
            if (Ui.Key("Disconnect", 0f, KeyStyle.Faint, true, "Drop the server. Bots still play offline."))
                _ = client.DisconnectAsync();
        }
        else if (Ui.Key("Connect", 0f, KeyStyle.Plain, client.State != ConnectionState.Connecting, $"Connect to {plugin.Config.ServerUrl}. Change it under Setup."))
        {
            _ = client.ConnectAsync(plugin.Config.ServerUrl);
        }
    }
}
