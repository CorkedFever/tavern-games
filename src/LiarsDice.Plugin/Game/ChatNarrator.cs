using LiarsDice.Core.Protocol;

namespace LiarsDice.Plugin.Game;

/// <summary>
/// Turns server messages into flavored, in-character lines for the local chat log.
/// Pure text only — it prints nothing to other players, so it's safe and ToS-clean.
/// Returns null for messages that shouldn't be narrated.
/// </summary>
public static class ChatNarrator
{
    private static readonly string[] Faces =
        { "", "ones", "twos", "threes", "fours", "fives", "sixes" };

    private static string Pip(int face) => face is >= 1 and <= 6 ? Faces[face] : $"{face}s";

    public static string? BuildLine(NetMessage message, GameSession session)
    {
        switch (message)
        {
            case RoundStarted r:
                return $"The dice are cupped and rolled. {session.NameOf(r.CurrentPlayerId)} opens the betting.";

            case BidPlaced b:
                return $"\"{b.Bid.Quantity} {Pip(b.Bid.FaceValue)},\" declares {session.NameOf(b.PlayerId)}.";

            case ChallengeResolved c:
            {
                var bid = $"{c.Bid.Quantity} {Pip(c.Bid.FaceValue)}";
                var verdict = c.BidWasValid
                    ? $"the table holds {c.ActualCount} — the bid stands!"
                    : $"only {c.ActualCount} show — a bluff!";
                var fate = c.LoserEliminated
                    ? $"{session.NameOf(c.LoserId)} loses their last die and is out of the game!"
                    : $"{session.NameOf(c.LoserId)} loses a die.";
                return $"Liar! {session.NameOf(c.ChallengerId)} doubts {session.NameOf(c.BidderId)} ({bid}) — {verdict} {fate}";
            }

            case GameEnded g:
                return $"{session.NameOf(g.WinnerId)} is the last gambler standing. Victory!";

            case ErrorMessage e:
                return $"[Liar's Dice] {e.Text}";

            default:
                return null;
        }
    }
}
