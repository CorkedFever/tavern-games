using System.Security.Cryptography;

namespace TavernGames.Core.Cards;

/// <summary>
/// A deck the server deals from. <see cref="Shuffled"/> uses the OS's cryptographic
/// generator with an unbiased Fisher-Yates shuffle: with real bluffing games on the
/// table, the order must not be guessable from earlier deals. <see cref="Stacked"/>
/// exists for tests, which need to know exactly what comes off the top.
/// </summary>
public sealed class Deck
{
    private readonly List<Card> _cards; // the top of the deck is the end of the list

    private Deck(List<Card> cards) => _cards = cards;

    public int Remaining => _cards.Count;

    public static IEnumerable<Card> AllCards() =>
        from suit in Enum.GetValues<Suit>()
        from rank in Enum.GetValues<Rank>()
        select new Card(rank, suit);

    public static Deck Shuffled()
    {
        var cards = AllCards().ToList();
        for (var i = cards.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        return new Deck(cards);
    }

    /// <summary>A seeded shuffle, for tests that need a repeatable deal. Never use it in real play.</summary>
    public static Deck Shuffled(Random rng)
    {
        var cards = AllCards().ToList();
        for (var i = cards.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        return new Deck(cards);
    }

    /// <summary>
    /// A deck that deals exactly <paramref name="topCards"/> first, in order, followed by
    /// the remaining cards in a fixed order. For tests.
    /// </summary>
    public static Deck Stacked(params string[] topCards)
    {
        var top = topCards.Select(Card.Parse).ToList();
        if (top.Distinct().Count() != top.Count)
            throw new ArgumentException("A stacked deck can't contain the same card twice.");

        var rest = AllCards().Where(c => !top.Contains(c));
        var cards = top.Concat(rest).Reverse().ToList(); // reversed: we draw from the end
        return new Deck(cards);
    }

    public Card Draw()
    {
        if (_cards.Count == 0)
            throw new InvalidOperationException("The deck is empty.");
        var card = _cards[^1];
        _cards.RemoveAt(_cards.Count - 1);
        return card;
    }
}
