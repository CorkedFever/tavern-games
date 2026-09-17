using Dalamud.Bindings.ImGui;
using TavernGames.Core;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// Venues: persistent groups with a join code, their own open tables, and a leaderboard.
/// Shows either the list of your venues or the one you've opened.
/// </summary>
internal sealed class VenuesTab(Plugin plugin, Action<string> hostTableFor)
{
    // List view forms.
    private string _joinCode = "";
    private string _newName = "";
    private string _newDescription = "";

    // Detail view state.
    private string? _boardGame;          // null = all games
    private string _editName = "";
    private string _editDescription = "";
    private string _editLoadedFor = "";
    private string _confirm = "";        // which destructive action is awaiting a second click

    public void Draw()
    {
        var account = plugin.Account;
        if (!account.HasProfile)
        {
            ImGui.TextWrapped("Venues need a player profile. Create one on the Profile tab.");
            return;
        }

        if (account.Venue is { } venue)
            DrawVenue(venue);
        else
            DrawList();
    }

    // -------------------------------------------------------------------- list

    private void DrawList()
    {
        var account = plugin.Account;

        ImGui.TextUnformatted("Your venues");
        if (account.Venues.Count == 0)
            ImGui.TextDisabled("None yet. Join one with a code, or start your own below.");

        foreach (var v in account.Venues)
        {
            if (ImGui.Selectable($"{v.Name}##{v.Id}"))
                Open(v.Id);
            ImGui.SameLine(260);
            ImGui.TextDisabled($"{RoleName(v.MyRole)}, {v.MemberCount} member{(v.MemberCount == 1 ? "" : "s")}");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Join a venue");
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Venue code", ref _joinCode, 8);
        ImGui.SameLine();
        if (ImGui.Button("Join##venue") && _joinCode.Trim().Length > 0)
        {
            plugin.Client.Send(new JoinVenue(_joinCode.Trim()));
            _joinCode = "";
        }
        ImGui.TextDisabled("A venue's staff can give you its code.");

        ImGui.Separator();
        ImGui.TextUnformatted("Start a venue");
        ImGui.InputText("Name##newvenue", ref _newName, 40);
        ImGui.InputText("Description##newvenue", ref _newDescription, 200);
        ImGui.BeginDisabled(_newName.Trim().Length < 3);
        if (ImGui.Button("Create venue"))
        {
            plugin.Client.Send(new CreateVenue(_newName, _newDescription));
            _newName = _newDescription = "";
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled("You'll get a join code to hand out. Staff you appoint can host tables for the venue.");
    }

    private void Open(string venueId)
    {
        _boardGame = null;
        _confirm = "";
        plugin.Client.Send(new GetVenue(venueId));
        plugin.Client.Send(new GetLeaderboard(venueId, null));
    }

    // ------------------------------------------------------------------ detail

    private void DrawVenue(VenueInfo venue)
    {
        var account = plugin.Account;
        var isStaff = venue.MyRole >= VenueRole.Staff;
        var isOwner = venue.MyRole == VenueRole.Owner;

        if (ImGui.SmallButton("< Venues"))
        {
            account.CloseVenue();
            return;
        }
        ImGui.SameLine();
        ImGui.TextColored(TableUi.Gold, venue.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"({RoleName(venue.MyRole)})");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##venue"))
        {
            plugin.Client.Send(new GetVenue(venue.Id));
            plugin.Client.Send(new GetLeaderboard(venue.Id, _boardGame));
        }

        if (venue.Description.Length > 0)
            ImGui.TextWrapped(venue.Description);

        if (venue.JoinCode is { } code)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Join code:");
            ImGui.SameLine();
            ImGui.TextColored(TableUi.Cyan, code);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText(code);
            ImGui.SameLine();
            if (ConfirmButton("New code", "code", "The old code stops working. Sure?"))
                plugin.Client.Send(new RegenerateVenueCode(venue.Id));
        }

        ImGui.Separator();
        DrawTables(venue, isStaff);

        ImGui.Separator();
        DrawLeaderboard(venue);

        ImGui.Separator();
        DrawMembers(venue, isStaff, isOwner);

        if (isOwner && ImGui.CollapsingHeader("Edit venue"))
        {
            var stamp = $"{venue.Id}|{venue.Name}|{venue.Description}";
            if (_editLoadedFor != stamp)
            {
                _editName = venue.Name;
                _editDescription = venue.Description;
                _editLoadedFor = stamp;
            }
            ImGui.InputText("Name##editvenue", ref _editName, 40);
            ImGui.InputText("Description##editvenue", ref _editDescription, 200);
            ImGui.BeginDisabled(_editName.Trim().Length < 3);
            if (ImGui.Button("Save venue"))
                plugin.Client.Send(new UpdateVenue(venue.Id, _editName, _editDescription));
            ImGui.EndDisabled();
        }

        ImGui.Separator();
        if (isOwner)
        {
            if (ConfirmButton("Delete venue...", "delete", "This removes the venue and its leaderboard for everyone."))
                plugin.Client.Send(new DeleteVenue(venue.Id));
        }
        else if (ConfirmButton("Leave venue...", "leave", "You'll need the code to come back."))
        {
            plugin.Client.Send(new LeaveVenue(venue.Id));
        }
    }

    private void DrawTables(VenueInfo venue, bool isStaff)
    {
        ImGui.TextUnformatted("Open tables");
        if (isStaff)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Host a table..."))
                hostTableFor(venue.Id);
        }

        if (venue.Tables.Length == 0)
        {
            ImGui.TextDisabled(isStaff ? "None right now. Host one to get things going." : "None right now. Staff open tables for the venue.");
            return;
        }

        foreach (var table in venue.Tables)
        {
            var game = GameCatalog.Find(table.GameType)?.DisplayName ?? table.GameType;
            var inLobby = table.Phase == GamePhase.Lobby;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{game}, hosted by {table.HostName}");
            ImGui.SameLine();
            ImGui.TextDisabled(inLobby ? $"{table.Players}/{table.MaxPlayers} seated" : "in progress");

            ImGui.SameLine();
            if (inLobby && table.Players < table.MaxPlayers)
            {
                if (ImGui.SmallButton($"Join##{table.RoomCode}"))
                    plugin.Client.Send(new JoinRoom(table.RoomCode, plugin.ResolvePlayerName()));
                ImGui.SameLine();
            }
            if (ImGui.SmallButton($"Watch##{table.RoomCode}"))
                plugin.Client.Send(new Spectate(table.RoomCode));
        }
    }

    private void DrawLeaderboard(VenueInfo venue)
    {
        var account = plugin.Account;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Leaderboard");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        var label = _boardGame is null ? "All games" : GameCatalog.Find(_boardGame)?.DisplayName ?? _boardGame;
        if (ImGui.BeginCombo("##boardgame", label))
        {
            if (ImGui.Selectable("All games", _boardGame is null))
                SetBoardGame(venue.Id, null);
            foreach (var game in GameCatalog.Games)
                if (ImGui.Selectable(game.DisplayName, _boardGame == game.Type))
                    SetBoardGame(venue.Id, game.Type);
            ImGui.EndCombo();
        }

        var rows = account.LeaderboardVenueId == venue.Id ? account.Leaderboard : [];
        if (rows.Length == 0)
        {
            ImGui.TextDisabled("No ranked games yet. A game counts once 2 or more real players sit down.");
            return;
        }

        if (ImGui.BeginTable("##board", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("Player");
            ImGui.TableSetupColumn("Won");
            ImGui.TableSetupColumn("Played");
            ImGui.TableSetupColumn("Win rate");
            ImGui.TableHeadersRow();

            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                var mine = row.ProfileId == account.Profile?.Id;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted((i + 1).ToString());
                ImGui.TableNextColumn();
                if (mine) ImGui.TextColored(TableUi.TurnGreen, row.DisplayName + " (you)");
                else ImGui.TextUnformatted(row.DisplayName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Won.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Played.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Played == 0 ? "-" : $"{100.0 * row.Won / row.Played:0}%");
            }
            ImGui.EndTable();
        }
    }

    private void SetBoardGame(string venueId, string? gameType)
    {
        _boardGame = gameType;
        plugin.Client.Send(new GetLeaderboard(venueId, gameType));
    }

    private void DrawMembers(VenueInfo venue, bool isStaff, bool isOwner)
    {
        if (!ImGui.CollapsingHeader($"Members ({venue.Members.Length})###members"))
            return;

        var myId = plugin.Account.Profile?.Id;
        foreach (var member in venue.Members)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(member.DisplayName + (member.ProfileId == myId ? " (you)" : ""));
            ImGui.SameLine();
            ImGui.TextDisabled(member.Tagline.Length > 0 ? $"{RoleName(member.Role)}, {member.Tagline}" : RoleName(member.Role));

            if (member.ProfileId == myId) continue;

            if (isOwner)
            {
                ImGui.SameLine();
                if (member.Role == VenueRole.Member)
                {
                    if (ImGui.SmallButton($"Make staff##{member.ProfileId}"))
                        plugin.Client.Send(new SetVenueRole(venue.Id, member.ProfileId, VenueRole.Staff));
                }
                else if (ImGui.SmallButton($"Remove staff##{member.ProfileId}"))
                {
                    plugin.Client.Send(new SetVenueRole(venue.Id, member.ProfileId, VenueRole.Member));
                }

                ImGui.SameLine();
                if (ConfirmButton($"Hand over##{member.ProfileId}", $"own{member.ProfileId}", $"{member.DisplayName} becomes the owner; you become staff."))
                    plugin.Client.Send(new SetVenueRole(venue.Id, member.ProfileId, VenueRole.Owner));
            }

            if (isStaff && member.Role < venue.MyRole)
            {
                ImGui.SameLine();
                if (ConfirmButton($"Remove##{member.ProfileId}", $"kick{member.ProfileId}", $"Remove {member.DisplayName} from the venue?"))
                    plugin.Client.Send(new KickFromVenue(venue.Id, member.ProfileId));
            }
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// A button for actions you can't take back: the first click arms it and shows a
    /// warning, the second click confirms. Returns true only on the confirming click.
    /// </summary>
    private bool ConfirmButton(string label, string key, string warning)
    {
        if (_confirm != key)
        {
            if (ImGui.SmallButton(label))
                _confirm = key;
            return false;
        }

        ImGui.TextColored(TableUi.Red, warning);
        var confirmed = ImGui.SmallButton($"Yes##{key}");
        ImGui.SameLine();
        var cancelled = ImGui.SmallButton($"No##{key}");
        if (confirmed || cancelled) _confirm = "";
        return confirmed;
    }

    private static string RoleName(VenueRole role) => role switch
    {
        VenueRole.Owner => "owner",
        VenueRole.Staff => "staff",
        _ => "member",
    };
}
