namespace TavernGames.Core.Cards;

public enum Suit
{
    Clubs,
    Diamonds,
    Hearts,
    Spades,
}

/// <summary>Numeric so ranks compare directly. Ace is high; games that need it low (blackjack, the wheel straight) handle that themselves.</summary>
public enum Rank
{
    Two = 2, Three, Four, Five, Six, Seven, Eight, Nine, Ten,
    Jack, Queen, King, Ace,
}

/// <summary>
/// One playing card. On the wire a card is its two-character <see cref="Code"/>
/// (rank then suit: "AS", "TD", "9H"), and a card the receiver isn't allowed to see is
/// sent as <see cref="HiddenCode"/>. Games never send a hidden card's real code.
/// </summary>
public readonly record struct Card(Rank Rank, Suit Suit)
{
    public const string HiddenCode = "??";

    private const string RankChars = "23456789TJQKA";
    private const string SuitChars = "CDHS";

    public string Code => $"{RankChars[(int)Rank - 2]}{SuitChars[(int)Suit]}";

    public static Card Parse(string code)
    {
        if (code is not { Length: 2 })
            throw new FormatException($"'{code}' is not a card code.");
        var rank = RankChars.IndexOf(char.ToUpperInvariant(code[0]));
        var suit = SuitChars.IndexOf(char.ToUpperInvariant(code[1]));
        if (rank < 0 || suit < 0)
            throw new FormatException($"'{code}' is not a card code.");
        return new Card((Rank)(rank + 2), (Suit)suit);
    }

    public static bool TryParse(string? code, out Card card)
    {
        card = default;
        if (code is not { Length: 2 }) return false;
        var rank = RankChars.IndexOf(char.ToUpperInvariant(code[0]));
        var suit = SuitChars.IndexOf(char.ToUpperInvariant(code[1]));
        if (rank < 0 || suit < 0) return false;
        card = new Card((Rank)(rank + 2), (Suit)suit);
        return true;
    }

    /// <summary>Spoken form, for logs and narration: "Nine of Clubs".</summary>
    public string Name => $"{RankName(Rank)} of {Suit}";

    public static string RankName(Rank rank) => rank switch
    {
        Rank.Ace => "Ace", Rank.King => "King", Rank.Queen => "Queen", Rank.Jack => "Jack",
        Rank.Ten => "Ten", Rank.Nine => "Nine", Rank.Eight => "Eight", Rank.Seven => "Seven",
        Rank.Six => "Six", Rank.Five => "Five", Rank.Four => "Four", Rank.Three => "Three",
        _ => "Two",
    };

    /// <summary>Spoken form of a wire code; a hidden or malformed code reads as "a face-down card".</summary>
    public static string NameOf(string? code) => TryParse(code, out var card) ? card.Name : "a face-down card";

    public override string ToString() => Code;
}
