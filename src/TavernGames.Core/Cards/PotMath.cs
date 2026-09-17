namespace TavernGames.Core.Cards;

/// <summary>A pot and the players still able to win it.</summary>
public sealed record Pot(int Amount, IReadOnlyList<string> Eligible);

/// <summary>What one seat put into the hand in total, and whether they folded.</summary>
public readonly record struct Stake(string PlayerId, int Contributed, bool Folded);

/// <summary>
/// Side-pot arithmetic for betting games. When a player goes all-in for less than the
/// others bet, they can only win what they matched from each opponent; the rest forms a
/// side pot contested by the players who covered it. Folded players' chips stay in the
/// pots but they can't win any of it.
/// </summary>
public static class PotMath
{
    public static List<Pot> BuildPots(IReadOnlyList<Stake> stakes)
    {
        var live = stakes.Where(s => !s.Folded && s.Contributed > 0).ToList();
        if (live.Count == 0)
        {
            // Nobody left to win it. With no chips in, there's simply no pot; with chips in, the
            // caller has folded everyone (a hand always has a last player standing), and quietly
            // returning nothing would make those chips vanish.
            if (stakes.Sum(s => s.Contributed) == 0) return [];
            throw new ArgumentException("There are chips in the pot but nobody left to win them.", nameof(stakes));
        }

        var pots = new List<Pot>();
        var previous = 0;
        foreach (var level in live.Select(s => s.Contributed).Distinct().OrderBy(x => x))
        {
            // Everyone (folded or not) chips in the slice of their stake that falls in this band.
            var amount = stakes.Sum(s => Math.Clamp(s.Contributed - previous, 0, level - previous));
            var eligible = live.Where(s => s.Contributed >= level).Select(s => s.PlayerId).ToList();
            pots.Add(new Pot(amount, eligible));
            previous = level;
        }

        // A folded player can have put in more than any live player; that overage goes to the last pot.
        var overage = stakes.Sum(s => Math.Max(0, s.Contributed - previous));
        if (overage > 0)
            pots[^1] = pots[^1] with { Amount = pots[^1].Amount + overage };

        // Bands with the same contenders are one pot as far as the players are concerned.
        var merged = new List<Pot>();
        foreach (var pot in pots)
        {
            if (merged.Count > 0 && merged[^1].Eligible.SequenceEqual(pot.Eligible))
                merged[^1] = merged[^1] with { Amount = merged[^1].Amount + pot.Amount };
            else
                merged.Add(pot);
        }
        return merged;
    }

    /// <summary>
    /// Pays each pot to the best hand among its eligible players, splitting ties evenly.
    /// Chips that don't divide go one at a time to the tied winners earliest in
    /// <paramref name="seatOrder"/> (by convention, closest to the dealer's left).
    /// </summary>
    public static Dictionary<string, int> Award(
        IReadOnlyList<Pot> pots,
        IReadOnlyDictionary<string, HandValue> hands,
        IReadOnlyList<string> seatOrder)
    {
        var winnings = new Dictionary<string, int>();
        foreach (var pot in pots)
        {
            var contenders = pot.Eligible.Where(hands.ContainsKey).ToList();
            if (contenders.Count == 0)
                throw new ArgumentException(
                    $"A pot of {pot.Amount} has no eligible player with a hand, so its chips would be lost.", nameof(hands));

            var best = contenders.Max(id => hands[id].Score);
            var winners = contenders
                .Where(id => hands[id].Score == best)
                .OrderBy(id => IndexOrEnd(seatOrder, id))
                .ToList();

            var share = pot.Amount / winners.Count;
            var odd = pot.Amount % winners.Count;
            for (var i = 0; i < winners.Count; i++)
                winnings[winners[i]] = winnings.GetValueOrDefault(winners[i]) + share + (i < odd ? 1 : 0);
        }
        return winnings;
    }

    private static int IndexOrEnd(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
            if (order[i] == id) return i;
        return int.MaxValue;
    }
}
