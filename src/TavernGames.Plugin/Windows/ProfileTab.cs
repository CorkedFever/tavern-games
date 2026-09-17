using Dalamud.Bindings.ImGui;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Windows;

/// <summary>Your player profile: the name and tagline others see, your record, and the way out.</summary>
internal sealed class ProfileTab(Plugin plugin)
{
    private string _name = "";
    private string _tagline = "";
    private string _loadedFor = "";
    private bool _confirmDelete;
    private bool _statsRequested;

    public void Draw()
    {
        var account = plugin.Account;
        if (account.Profile is not { } profile)
        {
            ImGui.TextWrapped("You're playing as a guest on this server. A profile keeps your record and lets you join venues.");
            if (ImGui.Button("Create my profile"))
                plugin.SayHello();
            _statsRequested = false;
            return;
        }

        // Load the form once per profile (and again after the server confirms a save).
        var stamp = $"{profile.Id}|{profile.DisplayName}|{profile.Tagline}";
        if (_loadedFor != stamp)
        {
            _name = profile.DisplayName;
            _tagline = profile.Tagline;
            _loadedFor = stamp;
        }

        if (!_statsRequested)
        {
            plugin.Client.Send(new GetStats());
            _statsRequested = true;
        }

        ImGui.InputText("Display name", ref _name, 24);
        ImGui.InputText("Tagline", ref _tagline, 60);
        ImGui.TextDisabled("Shown at tables, in venues and on leaderboards. e.g. \"the masked gambler\"");

        var changed = _name.Trim() != profile.DisplayName || _tagline.Trim() != profile.Tagline;
        ImGui.BeginDisabled(!changed || _name.Trim().Length == 0);
        if (ImGui.Button("Save profile"))
            plugin.Client.Send(new UpdateProfile(_name, _tagline));
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextUnformatted("Your record");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##stats"))
            plugin.Client.Send(new GetStats());

        if (account.Stats.Length == 0)
        {
            ImGui.TextDisabled("No finished games yet.");
        }
        else if (ImGui.BeginTable("##stats", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Game");
            ImGui.TableSetupColumn("Played");
            ImGui.TableSetupColumn("Won");
            ImGui.TableSetupColumn("Win rate");
            ImGui.TableHeadersRow();
            foreach (var stat in account.Stats)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(GameCatalog.Find(stat.GameType)?.DisplayName ?? stat.GameType);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stat.Played.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stat.Won.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stat.Played == 0 ? "-" : $"{100.0 * stat.Won / stat.Played:0}%");
            }
            ImGui.EndTable();
        }
        ImGui.TextDisabled("Your record counts every game. Venue leaderboards only count games with 2+ real players.");

        ImGui.Separator();
        ImGui.TextDisabled($"Profile id {profile.Id}. Your profile is tied to this plugin install on this server.");
        if (!_confirmDelete)
        {
            if (ImGui.SmallButton("Delete my profile..."))
                _confirmDelete = true;
        }
        else
        {
            ImGui.TextColored(TableUi.Red, "This erases your record and venue memberships for good.");
            if (ImGui.SmallButton("Yes, delete it"))
            {
                plugin.Client.Send(new DeleteProfile());
                _confirmDelete = false;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Keep it"))
                _confirmDelete = false;
        }
    }
}
