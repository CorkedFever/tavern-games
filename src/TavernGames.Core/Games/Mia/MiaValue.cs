namespace TavernGames.Core.Games.Mia;

/// <summary>
/// One throw of Mia's two dice, read as a two-digit number with the higher die as the
/// tens: a 5 and a 2 is "52". The ranking is the game's own and has nothing to do with
/// the number: the mixed rolls run 31 up to 65, then the six doubles, and 21 is "Mia",
/// which beats everything. This type is the only place that ordering lives, so the
/// engine, the bot and the plugin all rank values the same way.
///
/// The constructor takes the dice as they lie and does not validate them; build values
/// with <see cref="FromDice"/> or <see cref="FromCode"/> and check <see cref="IsValid"/>
/// for anything that came off the wire.
/// </summary>
public readonly record struct MiaValue(int High, int Low) : IComparable<MiaValue>
{
    /// <summary>Every legal value, lowest first. This array *is* the game's ranking.</summary>
    private static readonly int[] OrderedCodes =
    [
        31, 32, 41, 42, 43, 51, 52, 53, 54, 61, 62, 63, 64, 65, // the mixed rolls, read as numbers
        11, 22, 33, 44, 55, 66,                                 // then every double
        21,                                                     // and Mia, which nothing beats
    ];

    // Static initializers run in textual order, so these three deliberately follow OrderedCodes.
    private static readonly int[] RankByCode = BuildRanks();
    private static readonly int[] OutcomesAtOrAbove = BuildOutcomes();
    private static readonly MiaValue[] All = OrderedCodes.Select(FromCode).ToArray();

    /// <summary>How many equally likely ways two dice can land.</summary>
    public const int Rolls = 36;

    /// <summary>Every legal value in rank order, lowest first.</summary>
    public static IReadOnlyList<MiaValue> Ordered => All;

    public static MiaValue Mia => All[^1];

    /// <summary>The two-digit form used on the wire and in the UI: 52, 33, 21.</summary>
    public int Code => High * 10 + Low;

    /// <summary>Position in the ranking, 0 for 31 up to 20 for Mia; -1 for a value that is not legal.</summary>
    public int Rank => Code is >= 11 and <= 66 ? RankByCode[Code] : -1;

    public bool IsValid => Rank >= 0;
    public bool IsMia => High == 2 && Low == 1;
    public bool IsDouble => High == Low;

    public bool Beats(MiaValue other) => Rank > other.Rank;

    /// <summary>How many of the 36 rolls land on this value or better. The bot bets on these numbers.</summary>
    public int RollsAtLeast => Rank >= 0 ? OutcomesAtOrAbove[Rank] : 0;

    /// <summary>The exact chance that a fresh throw reaches this value.</summary>
    public double ChanceAtLeast => RollsAtLeast / (double)Rolls;

    /// <summary>Reads two dice as one value, higher die first.</summary>
    public static MiaValue FromDice(int a, int b) => a >= b ? new MiaValue(a, b) : new MiaValue(b, a);

    public static MiaValue FromCode(int code) =>
        TryFromCode(code, out var value) ? value : throw new ArgumentException($"{code} is not a Mia value.", nameof(code));

    public static bool TryFromCode(int code, out MiaValue value)
    {
        value = default;
        if (code is < 11 or > 66 || RankByCode[code] < 0) return false;
        value = new MiaValue(code / 10, code % 10);
        return true;
    }

    /// <summary>Spoken form for logs and buttons: "52", "double 4s", "Mia".</summary>
    public string Describe() => IsMia ? "Mia" : IsDouble ? $"double {High}s" : Code.ToString();

    public int CompareTo(MiaValue other) => Rank.CompareTo(other.Rank);

    public override string ToString() => Describe();

    private static int[] BuildRanks()
    {
        var ranks = new int[67];
        Array.Fill(ranks, -1);
        for (var i = 0; i < OrderedCodes.Length; i++)
            ranks[OrderedCodes[i]] = i;
        return ranks;
    }

    private static int[] BuildOutcomes()
    {
        // Walking down from Mia: a double can only be thrown one way, anything else two.
        var atLeast = new int[OrderedCodes.Length];
        var running = 0;
        for (var i = OrderedCodes.Length - 1; i >= 0; i--)
        {
            var code = OrderedCodes[i];
            running += code / 10 == code % 10 ? 1 : 2;
            atLeast[i] = running;
        }
        return atLeast;
    }
}
