using Dalamud.Bindings.ImGui;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;

namespace TavernGames.Plugin.Windows;

/// <summary>Joining a friend's table: a code, and whether you want a seat or a place on the rail.</summary>
internal sealed class JoinApp(Plugin plugin)
{
    private string _code = "";

    public void Draw()
    {
        var client = plugin.Client;
        var online = client.State == ConnectionState.Connected && !client.IsLocal;

        Theme.Heading("The code");
        Ui.Hint("Whoever opened the table has a 4-letter code. Sit down to play, or watch to see the table without a seat.");

        ImGui.SetNextItemWidth(140f);
        var entered = ImGui.InputTextWithHint("##joincode", "e.g. K7QX", ref _code, 8, ImGuiInputTextFlags.EnterReturnsTrue);
        var code = _code.Trim().ToUpperInvariant();

        ImGui.Dummy(new System.Numerics.Vector2(0f, 4f));
        if (!online)
        {
            Ui.Hint("Joining needs a server. Connect first.");
            if (Ui.Key("Connect", 200f, KeyStyle.Plain, client.State != ConnectionState.Connecting, $"Connect to {plugin.Config.ServerUrl}. Change it under Setup."))
                _ = client.ConnectAsync(plugin.Config.ServerUrl);
            return;
        }

        var ready = code.Length > 0;
        if (Ui.Key("Sit down", 200f, ready ? KeyStyle.Lit : KeyStyle.Plain, ready, "Take a seat at the table.") || (entered && ready))
            client.Send(new JoinRoom(code, plugin.ResolvePlayerName()));

        ImGui.SameLine();
        if (Ui.Key("Watch", 200f, KeyStyle.Plain, ready, "See the table from the rail. Anyone may watch."))
            client.Send(new Spectate(code));
    }
}
