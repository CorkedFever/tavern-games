using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Mia;

/// <summary>
/// The tavern's Mia player: it counts the 36 ways two dice can land, tells the truth
/// while the truth is good enough, and invents the smallest lie it thinks will pass.
/// Every number it leans on comes from <see cref="MiaValue.ChanceAtLeast"/>, shaded by
/// how hard the table had squeezed the announcer, and nudged by a little noise so a
/// human cannot read it off a table.
/// </summary>
public static class MiaBot
{
    // --- answering an announcement ---
    private const double CallBelow = 0.30;      // call when the claim looks less likely than this
    private const double PressureWeight = 0.35; // how much a high accepted value argues "they had to lie"
    private const double Noise = 0.18;          // +/- half of this on every read

    // --- answering a Mia ---
    private const double VolunteeredMia = 5.0;  // a Mia nobody was forced into is real more often than base rate
    private const double MiaNoise = 0.40;
    private const double TwoLivesCaution = 0.22;

    // --- announcing ---
    private const double BluffWithAGoodRoll = 0.12; // chance of claiming above an honest roll
    private const double SmallestLie = 0.55;        // share of lies that are the lowest legal claim
    private const double NextLieUp = 0.85;          // ...and of lies that are one notch above that

    /// <summary>The move for a bot that is on the clock. Always legal, whatever the table looks like.</summary>
    public static NetMessage Decide(MiaGame game, MiaPlayer self, Random rng) =>
        game.Step == MiaStep.Announce ? Announce(game, self, rng) : Respond(game, self, rng);

    private static NetMessage Announce(MiaGame game, MiaPlayer self, Random rng)
    {
        var legal = game.LegalAnnouncements;
        if (legal.Count == 1) return new MiaAnnounce(legal[0].Code); // a believed 66 leaves only Mia

        // Mia is always the top of the list, and bluffing it only buys a call and two lost
        // lives, so every invented claim stops one short of it.
        var boldest = legal.Count - 2;
        var roll = game.SecretFor(self.Id);

        if (roll is { } truth && (game.Accepted is not { } accepted || truth.Beats(accepted)))
        {
            // The truth will do. Now and then, dress it up: one notch above a good roll is
            // still believable, and it stops the table from reading every honest face.
            if (rng.NextDouble() >= BluffWithAGoodRoll) return new MiaAnnounce(truth.Code);

            var at = IndexOf(legal, truth);
            var dressed = Math.Min(at + 1 + rng.Next(0, 2), boldest);
            return new MiaAnnounce(legal[Math.Max(dressed, at)].Code);
        }

        // Caught short: something has to be invented. Usually the smallest claim that gets
        // past the table, because that is what an honest roll most often looks like.
        var pick = rng.NextDouble();
        var index = pick < SmallestLie ? 0
            : pick < NextLieUp ? Math.Min(1, boldest)
            : rng.Next(0, boldest + 1);
        return new MiaAnnounce(legal[Math.Clamp(index, 0, boldest)].Code);
    }

    private static NetMessage Respond(MiaGame game, MiaPlayer self, Random rng)
    {
        var announced = game.Announced!.Value;
        if (announced.IsMia) return AnswerMia(game, self, rng);

        return Believability(announced, game.Accepted, rng) < CallBelow
            ? new MiaCallLiar()
            : new MiaBelieve();
    }

    /// <summary>
    /// Mia is the one claim that cannot be raised. Calling a true one costs two lives while
    /// conceding costs one for certain, so the call only pays while the Mia looks less than
    /// half real. A player who had to get past 66 was going to say Mia whatever they threw,
    /// which makes that Mia a bluff far more often than one somebody chose to announce with
    /// the whole ladder still open to them.
    /// </summary>
    private static NetMessage AnswerMia(MiaGame game, MiaPlayer self, Random rng)
    {
        var real = MiaValue.Mia.ChanceAtLeast * (OnlyMiaWasLeft(game.Accepted) ? 1.0 : VolunteeredMia);

        // On two lives a called Mia is fatal where a concession is not, so the bot leans on
        // the cheap way out. On its last life both roads end the same, so it takes the chance.
        if (self.Lives == 2) real += TwoLivesCaution;
        real += (rng.NextDouble() - 0.5) * MiaNoise;

        return real > 0.5 ? new MiaConcede() : new MiaCallLiar();
    }

    /// <summary>
    /// The read on an announcement: the exact chance a fresh throw reaches it, shaded down
    /// by how far the table had already pushed the announcer, plus a little noise so the
    /// table cannot read the bot the way the bot reads them.
    /// </summary>
    private static double Believability(MiaValue announced, MiaValue? accepted, Random rng)
    {
        var honest = announced.ChanceAtLeast;
        var pressure = accepted is { } value ? 1.0 - value.ChanceAtLeast : 0.0;
        var noise = (rng.NextDouble() - 0.5) * Noise;
        return Math.Clamp(honest * (1.0 - PressureWeight * pressure) + noise, 0.0, 1.0);
    }

    /// <summary>True when the announcer's only legal claim was Mia, so the claim says nothing.</summary>
    private static bool OnlyMiaWasLeft(MiaValue? accepted) =>
        accepted is { } value && value.Rank == MiaValue.Mia.Rank - 1;

    private static int IndexOf(IReadOnlyList<MiaValue> values, MiaValue value)
    {
        for (var i = 0; i < values.Count; i++)
            if (values[i].Rank == value.Rank) return i;
        return 0;
    }
}
