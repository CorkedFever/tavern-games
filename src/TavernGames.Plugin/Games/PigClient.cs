using System.Numerics;
using Dalamud.Bindings.ImGui;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin.Games;

public sealed class PigClient : IClientGame
{
    public string GameType => PigModule.Type;
    public string CurrentPlayerId { get; private set; } = "";

    public int TurnTotal { get; private set; }
    public int TargetScore { get; private set; } = PigGame.DefaultTargetScore;

    // The most recent roll, shown until the next one so a bust stays visible for a moment.
    public int LastFace { get; private set; }
    public bool LastBusted { get; private set; }
    public string LastRollerId { get; private set; } = "";

    public void Reset()
    {
        CurrentPlayerId = LastRollerId = "";
        TurnTotal = LastFace = 0;
        LastBusted = false;
        TargetScore = PigGame.DefaultTargetScore;
    }

    public bool Apply(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case PigTurnStarted turn:
                CurrentPlayerId = turn.CurrentPlayerId;
                TurnTotal = turn.TurnTotal;
                TargetScore = turn.TargetScore;
                session.SetPlayers(turn.Players);
                return true;

            case PigRolled roll:
                LastFace = roll.Face;
                LastBusted = roll.Busted;
                LastRollerId = roll.PlayerId;
                TurnTotal = roll.TurnTotal;
                session.AddLog(roll.Busted
                    ? $"{session.NameOf(roll.PlayerId)} rolled a 1 and lost the lot."
                    : $"{session.NameOf(roll.PlayerId)} rolled a {roll.Face}. {roll.TurnTotal} riding.");
                return true;

            case PigHeld held:
                TurnTotal = 0;
                session.AddLog($"{session.NameOf(held.PlayerId)} held and banked {held.Banked} (now {held.NewScore}).");
                return true;

            default:
                return false;
        }
    }

    public string? Narrate(NetMessage message, GameSession session) => message switch
    {
        PigRolled { Busted: true } r =>
            $"A one! {session.NameOf(r.PlayerId)} groans as the whole pot slips away.",
        PigRolled r when r.TurnTotal >= 20 =>
            $"{session.NameOf(r.PlayerId)} rolls a {r.Face}. {r.TurnTotal} riding now. Bold.",
        PigRolled r =>
            $"{session.NameOf(r.PlayerId)} rolls a {r.Face}. {r.TurnTotal} riding.",
        PigHeld h =>
            $"{session.NameOf(h.PlayerId)} scoops up {h.Banked} and sits on {h.NewScore}.",
        _ => null,
    };

    public void DrawSeatTally(PlayerPublic seat)
    {
        var fraction = TargetScore > 0 ? Math.Clamp(seat.Tally / (float)TargetScore, 0f, 1f) : 0f;
        ImGui.ProgressBar(fraction, new Vector2(150, 0), $"{seat.Tally} / {TargetScore}");
    }

    public void DrawTable(GameSession session, Action<NetMessage> send)
    {
        TableUi.Roster(session);
        ImGui.Separator();

        // The last die rolled, with the pot beside it.
        ImGui.AlignTextToFramePadding();
        if (LastFace > 0)
        {
            DiceRenderer.Die(LastFace, 44f);
            ImGui.SameLine(0, 12);
        }

        ImGui.BeginGroup();
        if (LastBusted && LastRollerId.Length > 0)
            ImGui.TextColored(TableUi.Red, $"Bust! {session.NameOf(LastRollerId)} rolled a 1.");
        else if (LastFace > 0)
            ImGui.TextUnformatted($"{session.NameOf(LastRollerId)} rolled a {LastFace}.");
        else
            ImGui.TextDisabled("No rolls yet.");

        ImGui.TextUnformatted("Riding this turn:");
        ImGui.SameLine();
        ImGui.TextColored(TableUi.Gold, TurnTotal.ToString());
        ImGui.EndGroup();

        ImGui.Separator();

        var myTurn = session.AmActivePlayer && CurrentPlayerId == session.MyId;
        if (myTurn)
        {
            ImGui.TextColored(TableUi.TurnGreen, "Your turn!");
            if (ImGui.Button("Roll"))
                send(new PigRoll());

            ImGui.SameLine();
            ImGui.BeginDisabled(TurnTotal == 0);
            if (ImGui.Button($"Hold and bank {TurnTotal}##hold"))
                send(new PigHold());
            ImGui.EndDisabled();

            var mine = session.Players.FirstOrDefault(p => p.Id == session.MyId)?.Tally ?? 0;
            if (TurnTotal > 0 && mine + TurnTotal >= TargetScore)
                ImGui.TextColored(TableUi.Gold, "Hold now and you win!");
        }
        else if (!session.IsSpectator)
        {
            ImGui.TextDisabled($"Waiting for {session.NameOf(CurrentPlayerId)}...");
        }
    }
}
