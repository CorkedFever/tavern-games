using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// Venues: persistent groups with a join code, their own open tables, and a leaderboard.
/// Shows either the list of your venues or the one you've opened.
/// </summary>
internal sealed class VenuesApp(Plugin plugin, Action<string> hostTableFor)
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
            Ui.Hint("Venues need a player profile on this server. Connect, and the server gives you one.");
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

        Theme.Heading("Your venues");
        if (account.Venues.Count == 0)
            Ui.Hint("None yet. Join one with a code, or start your own below.");

        foreach (var v in account.Venues)
        {
            if (ImGui.Selectable($"{v.Name}##{v.Id}"))
                Open(v.Id);
            ImGui.SameLine(260);
            ImGui.TextColored(Theme.TextFaint, $"{RoleName(v.MyRole)}, {v.MemberCount} member{(v.MemberCount == 1 ? "" : "s")}");
        }

        Theme.Heading("Join a venue");
        ImGui.SetNextItemWidth(140f);
        var entered = ImGui.InputTextWithHint("##venuecode", "venue code", ref _joinCode, 8, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Join##venue") || entered) && _joinCode.Trim().Length > 0)
        {
            plugin.Client.Send(new JoinVenue(_joinCode.Trim()));
            _joinCode = "";
        }
        Ui.Hint("A venue's staff can give you its code.");

        Theme.Heading("Start a venue");
        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("Name##newvenue", "The Drowning Wench", ref _newName, 40);
        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("Description##newvenue", "open Fridays from eight bells", ref _newDescription, 200);
        ImGui.BeginDisabled(_newName.Trim().Length < 3);
        if (ImGui.Button("Create venue"))
        {
            plugin.Client.Send(new CreateVenue(_newName, _newDescription));
            _newName = _newDescription = "";
        }
        ImGui.EndDisabled();
        Ui.Hint("You'll get a join code to hand out. Staff you appoint can host tables for the venue.");
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

        ImGui.AlignTextToFramePadding();
        if (ImGui.SmallButton("< Venues"))
        {
            account.CloseVenue();
            return;
        }
        ImGui.SameLine(0f, 10f);
        ImGui.TextColored(Theme.Accent, venue.Name);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextFaint, $"{RoleName(venue.MyRole)} · {venue.Members.Length} member{(venue.Members.Length == 1 ? "" : "s")}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##venue"))
        {
            plugin.Client.Send(new GetVenue(venue.Id));
            plugin.Client.Send(new GetLeaderboard(venue.Id, _boardGame));
        }

        if (venue.Description.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            ImGui.TextWrapped(venue.Description);
            ImGui.PopStyleColor();
        }

        if (venue.JoinCode is { } code)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextFaint, "Join code");
            ImGui.SameLine();
            Theme.Displayed(Theme.Accent, code);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText(code);
            Ui.Tip("Hand it to whoever should be a member.");
            ImGui.SameLine();
            if (ConfirmButton("New code", "code", "The old code stops working. Sure?"))
                plugin.Client.Send(new RegenerateVenueCode(venue.Id));
        }

        DrawTables(venue, isStaff);
        DrawLeaderboard(venue);
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
            ImGui.SetNextItemWidth(220f);
            ImGui.InputText("Name##editvenue", ref _editName, 40);
            ImGui.SetNextItemWidth(220f);
            ImGui.InputText("Description##editvenue", ref _editDescription, 200);
            ImGui.BeginDisabled(_editName.Trim().Length < 3);
            if (ImGui.Button("Save venue"))
                plugin.Client.Send(new UpdateVenue(venue.Id, _editName, _editDescription));
            ImGui.EndDisabled();
        }

        ImGui.Dummy(new Vector2(0f, 8f));
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
        Theme.Heading("Open tables");
        if (isStaff)
        {
            if (ImGui.SmallButton("Host a table..."))
                hostTableFor(venue.Id);
            Ui.Tip("Open a table for this venue: members find it here, and the result counts on the leaderboard.");
        }

        if (venue.Tables.Length == 0)
        {
            Ui.Hint(isStaff ? "None right now. Host one to get things going." : "None right now. Staff open tables for the venue.");
            return;
        }

        foreach (var table in venue.Tables)
        {
            var game = GameCatalog.Find(table.GameType)?.DisplayName ?? table.GameType;
            var inLobby = table.Phase == GamePhase.Lobby;
            var open = inLobby && table.Players < table.MaxPlayers;

            ImGui.AlignTextToFramePadding();
            Ui.Dot(open ? Theme.Good : inLobby ? Theme.TextFaint : Theme.Accent, open ? "seats free" : inLobby ? "full" : "in progress");
            ImGui.SameLine(0f, 4f);
            ImGui.TextUnformatted(game);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, $"hosted by {table.HostName}");
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextFaint, inLobby ? $"{table.Players}/{table.MaxPlayers} seated" : "in progress");

            ImGui.SameLine();
            if (open)
            {
                if (ImGui.SmallButton($"Sit down##{table.RoomCode}"))
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
        Theme.Heading("Leaderboard");

        var labels = new List<string> { "All games" };
        labels.AddRange(GameCatalog.Games.Select(g => g.DisplayName));
        var selected = _boardGame is null ? 0 : 1 + GameCatalog.Games.ToList().FindIndex(g => g.Type == _boardGame);
        var pressed = Ui.Chips("board", labels, selected);
        if (pressed >= 0)
            SetBoardGame(venue.Id, pressed == 0 ? null : GameCatalog.Games[pressed - 1].Type);

        var rows = account.LeaderboardVenueId == venue.Id ? account.Leaderboard : [];
        if (rows.Length == 0)
        {
            Ui.Hint("No ranked games yet. A game counts once 2 or more real players sit down.");
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
                if (mine) ImGui.TextColored(Theme.Accent, row.DisplayName + " (you)");
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
        ImGui.Dummy(new Vector2(0f, 6f));
        if (!ImGui.CollapsingHeader($"Members ({venue.Members.Length})###members"))
            return;

        var myId = plugin.Account.Profile?.Id;
        foreach (var member in venue.Members)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(member.DisplayName + (member.ProfileId == myId ? " (you)" : ""));
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextFaint, member.Tagline.Length > 0 ? $"{RoleName(member.Role)}, {member.Tagline}" : RoleName(member.Role));

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

        ImGui.TextColored(Theme.Bad, warning);
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
