using TavernGames.Core.Cards;

namespace TavernGames.Core.Tests;

public class CardKitTests
{
    private static Card[] Cards(string codes) => codes.Split(' ').Select(Card.Parse).ToArray();
    private static HandValue Hand(string codes) => PokerHand.Evaluate(Cards(codes));

    // ------------------------------------------------------------------- cards

    [Fact]
    public void CardCodes_RoundTrip_ForTheWholeDeck()
    {
        var all = Deck.AllCards().ToList();
        Assert.Equal(52, all.Distinct().Count());
        Assert.All(all, c => Assert.Equal(c, Card.Parse(c.Code)));
        Assert.Equal("TD", new Card(Rank.Ten, Suit.Diamonds).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1S")]
    [InlineData("AX")]
    [InlineData("??")]
    [InlineData("10H")]
    public void BadCodes_AreRejected(string code)
    {
        Assert.False(Card.TryParse(code, out _));
        Assert.Throws<FormatException>(() => Card.Parse(code));
    }

    [Fact]
    public void AShuffledDeck_DealsEveryCardExactlyOnce()
    {
        var deck = Deck.Shuffled();
        var dealt = Enumerable.Range(0, 52).Select(_ => deck.Draw()).ToList();

        Assert.Equal(52, dealt.Distinct().Count());
        Assert.Equal(0, deck.Remaining);
        Assert.Throws<InvalidOperationException>(() => deck.Draw());
    }

    [Fact]
    public void Shuffles_Differ()
    {
        // 52! orderings: two shuffles matching would mean the shuffle isn't happening.
        var a = Deck.Shuffled(); var b = Deck.Shuffled();
        var first = Enumerable.Range(0, 52).Select(_ => a.Draw()).ToList();
        var second = Enumerable.Range(0, 52).Select(_ => b.Draw()).ToList();
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AStackedDeck_DealsItsTopCardsInOrder_ThenTheRest()
    {
        var deck = Deck.Stacked("AS", "KH", "2C");

        Assert.Equal(Cards("AS KH 2C"), new[] { deck.Draw(), deck.Draw(), deck.Draw() });
        Assert.Equal(49, deck.Remaining);
        Assert.Throws<ArgumentException>(() => Deck.Stacked("AS", "AS"));
    }

    // ------------------------------------------------------------ hand ranking

    [Theory]
    [InlineData("AS KS QS JS TS", HandCategory.StraightFlush)]
    [InlineData("9H 9D 9S 9C 2D", HandCategory.FourOfAKind)]
    [InlineData("KH KD KS 3C 3D", HandCategory.FullHouse)]
    [InlineData("AH 9H 7H 4H 2H", HandCategory.Flush)]
    [InlineData("9H 8D 7S 6C 5D", HandCategory.Straight)]
    [InlineData("5H 4D 3S 2C AD", HandCategory.Straight)]
    [InlineData("QH QD QS 8C 2D", HandCategory.ThreeOfAKind)]
    [InlineData("QH QD 8S 8C 2D", HandCategory.TwoPair)]
    [InlineData("QH QD 9S 8C 2D", HandCategory.Pair)]
    [InlineData("AH QD 9S 8C 2D", HandCategory.HighCard)]
    public void Categories_AreRecognised(string cards, HandCategory expected) =>
        Assert.Equal(expected, Hand(cards).Category);

    [Fact]
    public void Categories_RankInTheStandardOrder()
    {
        var ladder = new[]
        {
            "AH QD 9S 8C 2D", "QH QD 9S 8C 2D", "QH QD 8S 8C 2D", "QH QD QS 8C 2D", "9H 8D 7S 6C 5D",
            "AH 9H 7H 4H 2H", "KH KD KS 3C 3D", "9H 9D 9S 9C 2D", "9S 8S 7S 6S 5S",
        }.Select(Hand).ToList();

        for (var i = 1; i < ladder.Count; i++)
            Assert.True(ladder[i] > ladder[i - 1], $"hand {i} should beat hand {i - 1}");
    }

    [Fact]
    public void Kickers_BreakTies()
    {
        Assert.True(Hand("KH KD AS 7C 2D") > Hand("KS KC QS 7D 2H"));   // pair of kings: ace kicker beats queen
        Assert.True(Hand("QH QD 8S 8C AD") > Hand("QS QC 8H 8D KD"));   // two pair: the fifth card decides
        Assert.True(Hand("AH JH 9H 4H 2H") > Hand("AS JS 8S 7S 6S"));   // flushes compare card by card
        Assert.True(Hand("KH KD KS 2C 2D") > Hand("QH QD QS AC AD"));   // full house: the trips matter first
    }

    [Fact]
    public void TheWheel_IsTheLowestStraight()
    {
        var wheel = Hand("5H 4D 3S 2C AD");
        Assert.True(Hand("6H 5D 4S 3C 2D") > wheel);
        Assert.True(wheel > Hand("AH AD AS KC QD"));                     // still a straight: beats trips
        Assert.NotEqual(HandCategory.Straight, Hand("QH KD AS 2C 3D").Category); // no wrapping around the ace
    }

    [Fact]
    public void SuitsNeverBreakTies()
    {
        Assert.Equal(Hand("AH KH QD JS 9C"), Hand("AS KD QC JH 9D"));
        Assert.Equal(0, Hand("AH KH QD JS 9C").CompareTo(Hand("AS KD QC JH 9D")));
    }

    [Fact]
    public void SevenCards_FindTheBestFive()
    {
        // Hole AS KS on a board of QS JS TS 2D 2C: the royal flush, not the pair of twos.
        var value = Hand("AS KS QS JS TS 2D 2C");
        Assert.Equal(HandCategory.StraightFlush, value.Category);
        Assert.Equal(5, value.BestFive.Length);
        Assert.All(value.BestFive, c => Assert.Equal(Suit.Spades, c.Suit));

        // Board plays: both players hold nothing better than the board's straight, so they tie.
        Assert.Equal(Hand("2H 3D 9S 8C 7D 6H 5S"), Hand("2C 2S 9S 8C 7D 6H 5S"));
    }

    [Theory]
    [InlineData("AS KS QS JS TS", "Royal flush")]
    [InlineData("KH KD KS 3C 3D", "Full house, Kings full of Threes")]
    [InlineData("5H 4D 3S 2C AD", "Straight, Five high")]
    [InlineData("QH QD 8S 8C 2D", "Two pair, Queens and Eights")]
    [InlineData("6H 6D 9S 8C 2D", "Pair of Sixes")]
    [InlineData("AH QD 9S 8C 2D", "Ace high")]
    public void Hands_AreDescribedInTableTalk(string cards, string expected) =>
        Assert.Equal(expected, PokerHand.Describe(Hand(cards)));

    [Fact]
    public void Evaluate_RejectsBadInput()
    {
        Assert.Throws<ArgumentException>(() => PokerHand.Evaluate(Cards("AS KS QS JS")));
        Assert.Throws<ArgumentException>(() => PokerHand.Evaluate(Cards("AS AS QS JS TS")));
    }

    // -------------------------------------------------------------------- pots

    [Fact]
    public void EqualStakes_MakeOnePot()
    {
        var pots = PotMath.BuildPots([new("a", 100, false), new("b", 100, false), new("c", 100, false)]);

        var pot = Assert.Single(pots);
        Assert.Equal(300, pot.Amount);
        Assert.Equal(["a", "b", "c"], pot.Eligible);
    }

    [Fact]
    public void AShortAllIn_CreatesASidePot_TheyCannotWin()
    {
        // a is all-in for 50; b and c bet 200 each.
        var pots = PotMath.BuildPots([new("a", 50, false), new("b", 200, false), new("c", 200, false)]);

        Assert.Equal(2, pots.Count);
        Assert.Equal((150, 3), (pots[0].Amount, pots[0].Eligible.Count));   // 50 from each
        Assert.Equal(300, pots[1].Amount);                                  // the other 150 from b and c
        Assert.Equal(["b", "c"], pots[1].Eligible);
    }

    [Fact]
    public void FoldedChips_StayInThePot_ButTheFolderCannotWin()
    {
        var pots = PotMath.BuildPots([new("a", 100, false), new("b", 100, false), new("c", 60, true)]);

        var pot = Assert.Single(pots);
        Assert.Equal(260, pot.Amount);
        Assert.DoesNotContain("c", pot.Eligible);
    }

    [Fact]
    public void Pots_AlwaysAddUpToWhatWentIn()
    {
        var rng = new Random(11);
        for (var trial = 0; trial < 500; trial++)
        {
            var stakes = Enumerable.Range(0, rng.Next(2, 7))
                .Select(i => new Stake($"p{i}", rng.Next(0, 300), rng.Next(3) == 0))
                .ToList();
            if (stakes.All(s => s.Folded || s.Contributed == 0)) continue;

            var pots = PotMath.BuildPots(stakes);
            Assert.Equal(stakes.Sum(s => s.Contributed), pots.Sum(p => p.Amount));
            Assert.All(pots, p => Assert.NotEmpty(p.Eligible));
        }
    }

    [Fact]
    public void ChipsAreNeverSilentlyLost()
    {
        // Everyone folded with chips in: that can't happen in a real hand, so it must not pass quietly.
        Assert.Throws<ArgumentException>(() =>
            PotMath.BuildPots([new("a", 100, true), new("b", 100, true), new("c", 0, false)]));
        Assert.Empty(PotMath.BuildPots([new("a", 0, false), new("b", 0, true)])); // nothing in, nothing to build

        // A pot whose only eligible player has no hand on record.
        var pots = new List<Pot> { new(300, ["a"]) };
        Assert.Throws<ArgumentException>(() => PotMath.Award(pots, new Dictionary<string, HandValue>(), ["a"]));
    }

    [Fact]
    public void TheBestHand_WinsOnlyThePotsItIsEligibleFor()
    {
        var pots = PotMath.BuildPots([new("short", 50, false), new("big", 200, false), new("mid", 200, false)]);
        var hands = new Dictionary<string, HandValue>
        {
            ["short"] = Hand("AS AH AD KC KD"), // best hand, but only covered 50
            ["big"] = Hand("QS QH 9D 8C 2D"),
            ["mid"] = Hand("JS JH 9C 8D 2H"),
        };

        var won = PotMath.Award(pots, hands, ["short", "big", "mid"]);

        Assert.Equal(150, won["short"]);  // main pot
        Assert.Equal(300, won["big"]);    // side pot goes to the better of the two who covered it
        Assert.False(won.ContainsKey("mid"));
    }

    [Fact]
    public void Ties_SplitThePot_AndTheOddChipGoesToTheEarliestSeat()
    {
        var pots = new List<Pot> { new(101, ["a", "b"]) };
        var hands = new Dictionary<string, HandValue>
        {
            ["a"] = Hand("AH KH QD JS 9C"),
            ["b"] = Hand("AS KD QC JH 9D"),
        };

        var won = PotMath.Award(pots, hands, ["b", "a"]); // b sits first

        Assert.Equal(51, won["b"]);
        Assert.Equal(50, won["a"]);
    }
}
