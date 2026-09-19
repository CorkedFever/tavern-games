using Dalamud.Bindings.ImGui;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// Your player profile: the name and tagline you show at tables, and your record. It is
/// local-first, so it works with no server. Your name and tagline live in the plugin
/// config and are used at every table; when you're connected to a server, saving also
/// pushes them up so leaderboards show the same name. Your record is kept per game on
/// this device and counts offline games too.
/// </summary>
internal sealed class ProfileTab(Plugin plugin)
{
    private string _name = "";
    private string _tagline = "";
    private bool _loaded;

    public void Draw()
    {
        var config = plugin.Config;
        if (!_loaded)
        {
            _name = config.PlayerName;
            _tagline = config.Tagline;
            _loaded = true;
        }

        ImGui.TextColored(TableUi.Gold, "Player profile");
        ImGui.Spacing();

        ImGui.InputText("Display name", ref _name, 24);
        ImGui.TextDisabled("Leave blank to use your character name.");
        ImGui.InputText("Tagline", ref _tagline, 60);
        ImGui.TextDisabled("Shown under your name. e.g. \"the masked gambler\"");

        var online = plugin.Client.State == Game.ConnectionState.Connected && !plugin.Client.IsLocal;
        var changed = _name.Trim() != config.PlayerName || _tagline.Trim() != config.Tagline;

        ImGui.BeginDisabled(!changed);
        if (ImGui.Button("Save"))
        {
            config.PlayerName = _name.Trim();
            config.Tagline = _tagline.Trim();
            config.Save();
            _loaded = false; // re-read the trimmed values back into the fields
            if (online && plugin.Account.HasProfile)
                plugin.Client.Send(new UpdateProfile(config.PlayerName, config.Tagline));
        }
        ImGui.EndDisabled();

        if (online)
            ImGui.TextDisabled("Connected: saving also updates your name on this server.");

        ImGui.Separator();
        DrawRecord();
    }

    private void DrawRecord()
    {
        var config = plugin.Config;
        ImGui.TextUnformatted("Your record");
        ImGui.SameLine();
        ImGui.TextDisabled("(this device, offline games included)");

        var rows = GameCatalog.Games
            .Select(g => (Game: g, Played: config.LocalPlayed.GetValueOrDefault(g.Type), Won: config.LocalWon.GetValueOrDefault(g.Type)))
            .Where(r => r.Played > 0)
            .ToList();

        if (rows.Count == 0)
        {
            ImGui.TextDisabled("No finished games yet. Play one against the bots!");
            return;
        }

        if (ImGui.BeginTable("##localrecord", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Game");
            ImGui.TableSetupColumn("Played");
            ImGui.TableSetupColumn("Won");
            ImGui.TableSetupColumn("Win rate");
            ImGui.TableHeadersRow();
            foreach (var (game, played, won) in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(game.DisplayName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(played.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(won.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{100.0 * won / played:0}%");
            }
            ImGui.EndTable();
        }
    }
}
