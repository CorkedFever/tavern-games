namespace TavernGames.Core.Cards;

public enum HandCategory
{
    HighCard,
    Pair,
    TwoPair,
    ThreeOfAKind,
    Straight,
    Flush,
    FullHouse,
    FourOfAKind,
    StraightFlush,
}

/// <summary>
/// The strength of a poker hand. Two values compare exactly as the hands would at a
/// showdown: category first, then the ranks that break ties within it (so a pair of
/// kings with an ace kicker beats a pair of kings with a queen). Equal values split the pot.
/// </summary>
public readonly record struct HandValue(HandCategory Category, long Score, Card[] BestFive) : IComparable<HandValue>
{
    public int CompareTo(HandValue other) => Score.CompareTo(other.Score);

    // Card[] has reference equality; two hands are "the same strength" when their scores match.
    public bool Equals(HandValue other) => Score == other.Score;
    public override int GetHashCode() => Score.GetHashCode();

    public static bool operator >(HandValue a, HandValue b) => a.Score > b.Score;
    public static bool operator <(HandValue a, HandValue b) => a.Score < b.Score;
    public static bool operator >=(HandValue a, HandValue b) => a.Score >= b.Score;
    public static bool operator <=(HandValue a, HandValue b) => a.Score <= b.Score;
}

public static class PokerHand
{
    /// <summary>Evaluates the best five-card hand that can be made from five to seven cards.</summary>
    public static HandValue Evaluate(IReadOnlyList<Card> cards)
    {
        if (cards.Count is < 5 or > 7)
            throw new ArgumentException("A poker hand is evaluated from 5 to 7 cards.", nameof(cards));
        if (cards.Distinct().Count() != cards.Count)
            throw new ArgumentException("The same card appears twice.", nameof(cards));

        HandValue? best = null;
        var pick = new Card[5];
        foreach (var five in Combinations(cards, pick, 0, 0))
        {
            var value = EvaluateFive(five);
            if (best is null || value > best.Value)
                best = value with { BestFive = (Card[])five.Clone() };
        }
        return best!.Value;
    }

    /// <summary>A short, table-talk description: "Full house, Kings full of Threes".</summary>
    public static string Describe(HandValue value)
    {
        // Group ranks the same way the evaluator orders them: bigger groups first, then higher ranks.
        var groups = value.BestFive
            .GroupBy(c => c.Rank)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .Select(g => g.Key)
            .ToArray();

        var straightHigh = IsWheel(value.BestFive) ? Rank.Five : value.BestFive.Max(c => c.Rank);

        return value.Category switch
        {
            HandCategory.StraightFlush when straightHigh == Rank.Ace => "Royal flush",
            HandCategory.StraightFlush => $"Straight flush, {Name(straightHigh)} high",
            HandCategory.FourOfAKind => $"Four of a kind, {Plural(groups[0])}",
            HandCategory.FullHouse => $"Full house, {Plural(groups[0])} full of {Plural(groups[1])}",
            HandCategory.Flush => $"Flush, {Name(groups[0])} high",
            HandCategory.Straight => $"Straight, {Name(straightHigh)} high",
            HandCategory.ThreeOfAKind => $"Three of a kind, {Plural(groups[0])}",
            HandCategory.TwoPair => $"Two pair, {Plural(groups[0])} and {Plural(groups[1])}",
            HandCategory.Pair => $"Pair of {Plural(groups[0])}",
            _ => $"{Name(groups[0])} high",
        };
    }

    private static HandValue EvaluateFive(Card[] five)
    {
        // Ranks ordered the way ties are broken: larger groups first, then higher ranks.
        var groups = five
            .GroupBy(c => c.Rank)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .Select(g => (Rank: g.Key, Count: g.Count()))
            .ToArray();

        var flush = five.All(c => c.Suit == five[0].Suit);
        var wheel = IsWheel(five);
        var distinct = groups.Length == 5;
        var high = five.Max(c => c.Rank);
        var low = five.Min(c => c.Rank);
        var straight = distinct && (high - low == 4 || wheel);

        HandCategory category;
        if (straight && flush) category = HandCategory.StraightFlush;
        else if (groups[0].Count == 4) category = HandCategory.FourOfAKind;
        else if (groups[0].Count == 3 && groups[1].Count == 2) category = HandCategory.FullHouse;
        else if (flush) category = HandCategory.Flush;
        else if (straight) category = HandCategory.Straight;
        else if (groups[0].Count == 3) category = HandCategory.ThreeOfAKind;
        else if (groups[0].Count == 2 && groups[1].Count == 2) category = HandCategory.TwoPair;
        else if (groups[0].Count == 2) category = HandCategory.Pair;
        else category = HandCategory.HighCard;

        // In the wheel (A-2-3-4-5) the ace plays low, so the straight is only five-high.
        var tiebreak = straight
            ? [(int)(wheel ? Rank.Five : high)]
            : groups.Select(g => (int)g.Rank).ToArray();

        long score = (int)category;
        for (var i = 0; i < 5; i++)
            score = score * 15 + (i < tiebreak.Length ? tiebreak[i] : 0);

        return new HandValue(category, score, five);
    }

    private static bool IsWheel(IEnumerable<Card> five)
    {
        var ranks = five.Select(c => c.Rank).OrderBy(r => r).ToArray();
        return ranks.SequenceEqual([Rank.Two, Rank.Three, Rank.Four, Rank.Five, Rank.Ace]);
    }

    /// <summary>Yields every five-card subset. The same buffer is reused, so callers must copy what they keep.</summary>
    private static IEnumerable<Card[]> Combinations(IReadOnlyList<Card> cards, Card[] pick, int start, int depth)
    {
        if (depth == pick.Length)
        {
            yield return pick;
            yield break;
        }

        for (var i = start; i <= cards.Count - (pick.Length - depth); i++)
        {
            pick[depth] = cards[i];
            foreach (var combo in Combinations(cards, pick, i + 1, depth + 1))
                yield return combo;
        }
    }

    private static string Name(Rank rank) => Card.RankName(rank);

    private static string Plural(Rank rank) => rank == Rank.Six ? "Sixes" : Name(rank) + "s";
}
