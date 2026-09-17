namespace LiarsDice.Core;

/// <summary>A bot's chosen action for its turn: either raise with <see cref="Bid"/> or call liar.</summary>
public readonly record struct BotDecision(bool IsChallenge, Bid Bid)
{
    public static BotDecision Liar() => new(true, default);
    public static BotDecision Raise(Bid bid) => new(false, bid);
}

/// <summary>
/// A simple probabilistic Liar's Dice opponent. It reasons from its own hand plus the
/// expected distribution of every other (hidden) die — each unknown die shows the bid's
/// face with probability 1/6 — and challenges when the standing bid runs too far ahead
/// of that expectation. Otherwise it makes the most comfortable legal raise, with a dash
/// of randomness so it bluffs and isn't trivially readable.
/// </summary>
public static class BotStrategy
{
    // How far a bid may exceed the bot's expectation before it would rather call liar.
    private const double RaiseRiskTolerance = 1.25;

    public static BotDecision Decide(LiarsDiceGame game, Player self, Random rng)
    {
        var totalDice = game.Players.Where(p => !p.IsEliminated).Sum(p => p.DiceCount);
        var unknownDice = totalDice - self.DiceCount;

        var own = new int[Bid.MaxFace + 1];
        foreach (var d in self.Dice) own[d]++;

        // Expected number of dice showing face f across the whole table.
        double Expected(int face) => own[face] + unknownDice / 6.0;

        if (game.CurrentBid is not { } current)
            return BotDecision.Raise(OpeningBid(own, totalDice, unknownDice, rng));

        // Decide whether to challenge the standing bid.
        var surplus = current.Quantity - Expected(current.FaceValue);
        if (current.Quantity > totalDice)
            return BotDecision.Liar(); // strictly impossible to ever have this many

        // The further the bid is past expectation, the likelier we call it.
        var challengeChance = Math.Clamp((surplus - 0.5) / 2.0, 0.0, 0.95);
        if (rng.NextDouble() < challengeChance)
            return BotDecision.Liar();

        // Otherwise raise. Pick the cheapest legal raise measured against our own dice.
        var best = BestRaise(current, own, unknownDice);
        if (best is null)
            return BotDecision.Liar(); // no comfortable raise left

        var raise = best.Value;
        // Occasionally pad the quantity to bluff.
        if (rng.NextDouble() < 0.15)
            raise = raise with { Quantity = raise.Quantity + 1 };

        return BotDecision.Raise(raise);
    }

    private static Bid OpeningBid(ReadOnlySpan<int> own, int totalDice, int unknownDice, Random rng)
    {
        // Lead with our strongest face (ties broken toward the higher face).
        var face = 1;
        for (var f = Bid.MinFace; f <= Bid.MaxFace; f++)
            if (own[f] >= own[face]) face = f;

        var expected = own[face] + unknownDice / 6.0;
        var quantity = Math.Max(1, (int)Math.Round(expected));
        // Keep the opener modest so it has room to escalate.
        quantity = Math.Min(quantity, Math.Max(1, totalDice / 2));
        return new Bid(quantity, face);
    }

    /// <summary>
    /// Among the minimal legal raises (one per face) finds the one with the lowest "risk"
    /// — how far its quantity sits above what we'd expect that face to total. Returns null
    /// if even the safest option is too much of a stretch.
    /// </summary>
    private static Bid? BestRaise(Bid current, ReadOnlySpan<int> own, int unknownDice)
    {
        Bid? best = null;
        var bestRisk = double.MaxValue;

        for (var face = Bid.MinFace; face <= Bid.MaxFace; face++)
        {
            // Smallest legal raise that uses this face.
            var candidate = face > current.FaceValue
                ? new Bid(current.Quantity, face)       // same count, higher face
                : new Bid(current.Quantity + 1, face);  // must add a die

            var expected = own[face] + unknownDice / 6.0;
            var risk = candidate.Quantity - expected;

            if (risk < bestRisk)
            {
                bestRisk = risk;
                best = candidate;
            }
        }

        return bestRisk <= RaiseRiskTolerance ? best : null;
    }
}
