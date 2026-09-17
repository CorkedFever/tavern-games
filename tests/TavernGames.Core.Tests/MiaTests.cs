using TavernGames.Core.Games.Mia;
using TavernGames.Core.Games.Pig;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Tests;

public class MiaTests
{
    /// <summary>A started table. Dice are handed out two at a time, in the order the cup travels.</summary>
    private static MiaGame Started(int lives, int[] dice, params string[] ids)
    {
        var game = new MiaGame(new ScriptedRoller(dice), lives);
        foreach (var id in ids.Length > 0 ? ids : ["a", "b"])
            game.AddPlayer(id, id.ToUpperInvariant());
        game.StartGame();
        return game;
    }

    private static MiaModule Module(int lives, int[] dice, params string[] ids)
    {
        var module = new MiaModule(new ScriptedRoller(dice), lives);
        foreach (var id in ids.Length > 0 ? ids : ["a", "b"])
            module.AddPlayer(id, id.ToUpperInvariant(), isBot: false);
        return module;
    }

    private static int Lives(MiaGame game, string id) => game.Players.First(p => p.Id == id).Lives;

    private static MiaValue V(int code) => MiaValue.FromCode(code);

    // ------------------------------------------------------------------ values

    [Fact]
    public void TheRanking_RunsFromThirtyOneThroughTheDoublesToMia()
    {
        Assert.Equal(
            [31, 32, 41, 42, 43, 51, 52, 53, 54, 61, 62, 63, 64, 65, 11, 22, 33, 44, 55, 66, 21],
            MiaValue.Ordered.Select(v => v.Code));

        for (var i = 1; i < MiaValue.Ordered.Count; i++)
            Assert.True(MiaValue.Ordered[i].Beats(MiaValue.Ordered[i - 1]), $"{MiaValue.Ordered[i]} should beat {MiaValue.Ordered[i - 1]}");

        Assert.True(V(11).Beats(V(65)));        // the lowest double still beats the highest mixed roll
        Assert.True(MiaValue.Mia.Beats(V(66))); // and nothing beats Mia
        Assert.True(MiaValue.Mia.IsMia);
        Assert.True(V(44).IsDouble);
    }

    [Theory]
    [InlineData(5, 2, 52)]
    [InlineData(2, 5, 52)]
    [InlineData(4, 4, 44)]
    [InlineData(1, 2, 21)]
    [InlineData(1, 1, 11)]
    public void TwoDice_AreReadWithTheHigherOneAsTens(int a, int b, int code) =>
        Assert.Equal(code, MiaValue.FromDice(a, b).Code);

    [Theory]
    [InlineData(12)] // the dice would be read the other way round
    [InlineData(56)]
    [InlineData(10)]
    [InlineData(7)]
    [InlineData(77)]
    [InlineData(0)]
    public void ThingsTheDiceCannotShow_AreNotValues(int code)
    {
        Assert.False(MiaValue.TryFromCode(code, out _));
        Assert.Throws<ArgumentException>(() => MiaValue.FromCode(code));
    }

    [Fact]
    public void TheOdds_MatchAllThirtySixWaysTheDiceCanLand()
    {
        foreach (var value in MiaValue.Ordered)
        {
            var counted = 0;
            for (var a = 1; a <= 6; a++)
                for (var b = 1; b <= 6; b++)
                    if (!value.Beats(MiaValue.FromDice(a, b)))
                        counted++;

            Assert.Equal(counted, value.RollsAtLeast);
        }

        Assert.Equal(36, V(31).RollsAtLeast);   // every throw is at least the lowest value
        Assert.Equal(18, V(61).RollsAtLeast);   // exactly half the table
        Assert.Equal(3, V(66).RollsAtLeast);
        Assert.Equal(2, MiaValue.Mia.RollsAtLeast);
        Assert.Equal(2 / 36.0, MiaValue.Mia.ChanceAtLeast);
    }

    [Fact]
    public void ValuesSpeakPlainly()
    {
        Assert.Equal("52", V(52).Describe());
        Assert.Equal("double 4s", V(44).Describe());
        Assert.Equal("Mia", MiaValue.Mia.Describe());
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public void TheOpeningClaimIsFree_AndEveryLaterOneHasToClimb()
    {
        var game = Started(3, [4, 3, 2, 2]); // A holds 43, B will hold 22

        Assert.Throws<InvalidOperationException>(() => game.Announce("b", V(52))); // not B's cup
        game.Announce("a", V(52));                                                 // a lie, and perfectly legal
        game.Believe("b");

        Assert.Throws<InvalidOperationException>(() => game.Announce("b", V(52))); // the same is not higher
        Assert.Throws<InvalidOperationException>(() => game.Announce("b", V(51)));
        game.Announce("b", V(53));

        Assert.Equal(53, game.Announced?.Code);
        Assert.Equal(52, game.Accepted?.Code);
    }

    [Fact]
    public void BelievingTakesTheCup_AndTheBelieverRollsAfresh()
    {
        var game = Started(3, [4, 3, 6, 5]); // A holds 43, B will hold 65

        game.Announce("a", V(43));
        var events = game.Believe("b");

        Assert.Equal(MiaStep.Announce, game.Step);
        Assert.Equal("b", game.CurrentPlayerId);
        Assert.Equal(65, game.SecretFor("b")?.Code);
        Assert.Null(game.SecretFor("a"));    // A's dice are gone, and gone for good
        Assert.Contains(events, e => e.Kind == MiaEventKind.Rolled && e.PlayerId == "b");
    }

    [Fact]
    public void CallingAnHonestClaim_CostsTheCaller()
    {
        var exact = Started(3, [6, 5]); // A really does hold 65 and says so
        exact.Announce("a", V(65));
        var called = exact.CallLiar("b").First(e => e.Kind == MiaEventKind.Called);

        Assert.True(called.Honest);
        Assert.Equal(65, called.Revealed);
        Assert.Equal(("b", 1), (called.LoserId, called.LivesLost));
        Assert.Equal((3, 2), (Lives(exact, "a"), Lives(exact, "b")));

        var better = Started(3, [6, 6]); // understating the roll is honest too
        better.Announce("a", V(52));
        Assert.True(better.CallLiar("b").First(e => e.Kind == MiaEventKind.Called).Honest);
        Assert.Equal(2, Lives(better, "b"));
    }

    [Fact]
    public void CallingALie_CostsTheAnnouncer()
    {
        var game = Started(3, [3, 1]); // A holds 31 and claims double fives
        game.Announce("a", V(55));

        var called = game.CallLiar("b").First(e => e.Kind == MiaEventKind.Called);

        Assert.False(called.Honest);
        Assert.Equal((31, "a", 1), (called.Revealed, called.LoserId, called.LivesLost));
        Assert.Equal(2, Lives(game, "a"));
    }

    [Fact]
    public void AMiaIsWorthTwoLives_WhoeverTurnsOutToBeWrong()
    {
        var real = Started(3, [2, 1]); // A really has it
        real.Announce("a", MiaValue.Mia);
        var honest = real.CallLiar("b").First(e => e.Kind == MiaEventKind.Called);
        Assert.Equal(("b", 2), (honest.LoserId, honest.LivesLost));
        Assert.Equal(1, Lives(real, "b"));

        var bluff = Started(3, [6, 6]); // and here she does not
        bluff.Announce("a", MiaValue.Mia);
        var caught = bluff.CallLiar("b").First(e => e.Kind == MiaEventKind.Called);
        Assert.Equal(("a", 2), (caught.LoserId, caught.LivesLost));
        Assert.Equal(1, Lives(bluff, "a"));
    }

    [Fact]
    public void AMiaCannotBeBelieved_ButItCanBeConcededWithoutShowingTheDice()
    {
        var game = Started(3, [6, 6]); // a bluffed Mia
        game.Announce("a", MiaValue.Mia);

        Assert.Throws<InvalidOperationException>(() => game.Believe("b"));

        var events = game.Concede("b");
        var conceded = events.First(e => e.Kind == MiaEventKind.Conceded);

        Assert.Equal(("b", 1, false), (conceded.LoserId, conceded.LivesLost, conceded.LoserEliminated));
        Assert.Equal((3, 2), (Lives(game, "a"), Lives(game, "b")));
        Assert.All(events, e => Assert.Equal(0, e.Revealed)); // the cup stays shut
    }

    [Fact]
    public void ConcedingIsOnlyForAMia()
    {
        var game = Started(3, [6, 6]);
        game.Announce("a", V(66));
        Assert.Throws<InvalidOperationException>(() => game.Concede("b"));
    }

    [Fact]
    public void WhenSixesAreBelieved_OnlyMiaIsLeft_AndItStillWorks()
    {
        var game = Started(3, [6, 6, 4, 2]); // A holds 66 and says so, B will hold 42
        game.Announce("a", V(66));
        game.Believe("b");

        Assert.Equal(MiaValue.Mia, Assert.Single(game.LegalAnnouncements));

        game.Announce("b", MiaValue.Mia);
        var called = game.CallLiar("a").First(e => e.Kind == MiaEventKind.Called);

        Assert.Equal((42, false, "b", 2), (called.Revealed, called.Honest, called.LoserId, called.LivesLost));
    }

    [Fact]
    public void TheLoserOfTheLife_TakesTheCupNext()
    {
        var game = Started(3, [3, 1], "a", "b", "c");
        game.Announce("a", V(55));
        game.CallLiar("b");

        Assert.Equal(("a", MiaStep.Announce), (game.CurrentPlayerId, game.Step));
        Assert.Null(game.Accepted);
        Assert.Null(game.Announced);
    }

    [Fact]
    public void AnEliminatedLoser_HandsTheCupToTheNextPlayer()
    {
        var game = Started(1, [3, 1], "a", "b", "c"); // one life each
        game.Announce("a", V(55));

        var events = game.CallLiar("b");

        Assert.True(game.Players.First(p => p.Id == "a").IsOut);
        Assert.Equal(GamePhase.Playing, game.Phase);
        Assert.Contains(events, e => e.Kind == MiaEventKind.RoundStarted && e.PlayerId == "b");
        Assert.Equal("b", game.CurrentPlayerId);
    }

    [Fact]
    public void TheLastPlayerWithLives_Wins()
    {
        var game = Started(1, [3, 1]);
        game.Announce("a", V(55));

        var events = game.CallLiar("b");

        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal("b", game.WinnerId);
        Assert.Equal((MiaEventKind.GameOver, "b"), (events[^1].Kind, events[^1].WinnerId));
        Assert.Null(game.CurrentPlayerId);
    }

    [Fact]
    public void ATwoLifeMiaLoss_TakesNoMoreLivesThanThereAre()
    {
        var game = Started(1, [2, 1]); // A has a real Mia; B calls it on his last life
        game.Announce("a", MiaValue.Mia);

        game.CallLiar("b");

        Assert.Equal(0, Lives(game, "b")); // not -1
        Assert.Equal("a", game.WinnerId);
    }

    // --------------------------------------------------------------- departures

    [Fact]
    public void WhenTheAnnouncerWalksOut_TheRoundStartsAgainAndNobodyPays()
    {
        var game = Started(3, [4, 3], "a", "b", "c");
        game.Announce("a", V(52));

        var events = game.RemovePlayer("a");

        Assert.All(game.Players, p => Assert.Equal(3, p.Lives));
        Assert.Equal(("b", MiaStep.Announce), (game.CurrentPlayerId, game.Step));
        Assert.Null(game.Announced);
        Assert.Contains(events, e => e.Kind == MiaEventKind.RoundStarted && e.PlayerId == "b");
        Assert.Contains(events, e => e.Kind == MiaEventKind.Rolled && e.PlayerId == "b");
    }

    [Fact]
    public void WhenThePlayerOwingAnAnswerWalksOut_TheRoundStartsAgain()
    {
        var game = Started(3, [4, 3], "a", "b", "c");
        game.Announce("a", V(52)); // B is on the clock, owing an answer

        var events = game.RemovePlayer("b");

        Assert.All(game.Players, p => Assert.Equal(3, p.Lives));
        Assert.Equal(("c", MiaStep.Announce), (game.CurrentPlayerId, game.Step));
        Assert.Contains(events, e => e.Kind == MiaEventKind.RoundStarted && e.PlayerId == "c");
    }

    [Fact]
    public void WhenSomeoneOutsideTheExchangeWalksOut_TheRoundCarriesOn()
    {
        var game = Started(3, [4, 3], "a", "b", "c");
        game.Announce("a", V(52));

        Assert.Empty(game.RemovePlayer("c"));

        Assert.Equal((MiaStep.Respond, "b", 52), (game.Step, game.CurrentPlayerId, game.Announced?.Code));
        Assert.Equal(43, game.SecretFor("a")?.Code); // A still has her dice under the cup
    }

    [Fact]
    public void WhenEveryoneElseWalksOut_TheLastPlayerWins()
    {
        var game = Started(3, [4, 3]);

        var events = game.RemovePlayer("a");

        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal("b", game.WinnerId);
        Assert.Equal("b", Assert.Single(events).WinnerId);
    }

    [Fact]
    public void LeavingTheLobby_OrLeavingTwice_ChangesNothing()
    {
        var game = new MiaGame(new ScriptedRoller(3, 1), 3);
        game.AddPlayer("a", "A");
        game.AddPlayer("b", "B");

        Assert.Empty(game.RemovePlayer("a"));      // still in the lobby
        Assert.Empty(game.RemovePlayer("nobody")); // never sat down
        Assert.Single(game.Players);
    }

    // ------------------------------------------------------------------ module

    [Fact]
    public void TheModule_SendsTheDiceToTheRollerAlone_ThenTellsTheTable()
    {
        var module = Module(3, [5, 2]);

        var emits = module.Start();

        Assert.Collection(emits,
            e => Assert.Equal("a", Assert.IsType<MiaRoundStarted>(Assert.IsType<ToAll>(e).Message).PlayerId),
            e =>
            {
                var priv = Assert.IsType<ToPlayer>(e);
                Assert.Equal("a", priv.PlayerId);
                Assert.Equal(52, Assert.IsType<MiaYourRoll>(priv.Message).Value);
            },
            e => Assert.Equal("a", Assert.IsType<MiaRolled>(Assert.IsType<ToAll>(e).Message).PlayerId));
    }

    [Fact]
    public void TheModule_HoldsTheTableAfterAReveal_SoEveryoneCanRead()
    {
        var module = Module(3, [3, 1, 4, 4]);
        module.Start();
        module.Handle("a", new MiaAnnounce(55));

        var kinds = module.Handle("b", new MiaCallLiar()).Select(Describe).ToList();

        Assert.Equal(["called", "break", "round", "secret", "rolled"], kinds);
    }

    [Fact]
    public void TheModule_HoldsTheTableAfterAConcession_Too()
    {
        var module = Module(3, [6, 6, 4, 4]);
        module.Start();
        module.Handle("a", new MiaAnnounce(21));

        var kinds = module.Handle("b", new MiaConcede()).Select(Describe).ToList();

        Assert.Equal(["conceded", "break", "round", "secret", "rolled"], kinds);
    }

    [Fact]
    public void CatchUp_ShowsExactlyWhatTheTableIsLookingAt()
    {
        var module = Module(3, [5, 2]);
        module.Start();
        var announced = module.Handle("a", new MiaAnnounce(52))
            .OfType<ToAll>().Select(e => e.Message).OfType<MiaAnnounced>().Single();

        var catchUp = Assert.Single(module.CatchUp());

        // A latecomer is handed the same public table the seated players are looking at.
        Assert.Equal(new MiaSnapshot(announced.Table).Serialize(), catchUp.Serialize());
        var table = Assert.IsType<MiaSnapshot>(catchUp).Table;
        Assert.Equal((MiaStep.Respond, "b", "a", 52, 0), (table.Step, table.CurrentPlayerId, table.AnnouncerId, table.Announced, table.Accepted));
        Assert.Equal([3, 3], table.Seats.Select(s => s.Lives));
    }

    [Fact]
    public void CatchUp_SaysNothingBeforeTheGameStarts()
    {
        var module = Module(3, [5, 2]);
        Assert.Empty(module.CatchUp());
    }

    [Fact]
    public void ADepartingBystander_StillRedrawsTheTable()
    {
        var module = Module(3, [5, 2], "a", "b", "c");
        module.Start();
        module.Handle("a", new MiaAnnounce(52));

        var emits = module.RemovePlayer("c");

        var snapshot = Assert.IsType<MiaSnapshot>(Assert.IsType<ToAll>(Assert.Single(emits)).Message);
        Assert.Equal(["a", "b"], snapshot.Table.Seats.Select(s => s.PlayerId));
    }

    [Fact]
    public void TheModule_RefusesNonsense()
    {
        var module = Module(3, [5, 2]);
        module.Start();

        Assert.Throws<ArgumentException>(() => module.Handle("a", new MiaAnnounce(12)));
        Assert.Throws<ArgumentException>(() => module.Handle("a", new MiaAnnounce(0)));
        Assert.Throws<InvalidOperationException>(() => module.Handle("a", new PigRoll()));
        Assert.Throws<InvalidOperationException>(() => module.Handle("b", new MiaAnnounce(52)));
        Assert.Throws<InvalidOperationException>(() => module.Handle("a", new MiaBelieve()));
    }

    // -------------------------------------------------------------- hidden dice

    [Fact]
    public void ARollThatWasBelieved_IsNeverShownToAnyone()
    {
        // A rolls 31 and claims 43; B believes, rolls 66, and has to say Mia; A calls.
        var module = Module(2, [3, 1, 6, 6]);
        var emits = new List<Emit>(module.Start());
        emits.AddRange(module.Handle("a", new MiaAnnounce(43)));
        emits.AddRange(module.Handle("b", new MiaBelieve()));
        emits.AddRange(module.Handle("b", new MiaAnnounce(21)));
        emits.AddRange(module.Handle("a", new MiaCallLiar()));

        var called = emits.OfType<ToAll>().Select(e => e.Message).OfType<MiaCalled>().Single();
        Assert.Equal((66, false, "b", 2), (called.Revealed, called.Honest, called.LoserId, called.LivesLost));
        Assert.Equal(GamePhase.GameOver, module.Phase);

        // A's 31 went to A and to nobody else, and the game ended before another roll.
        Assert.Equal(31, emits.OfType<ToPlayer>()
            .Where(e => e.PlayerId == "a").Select(e => e.Message).OfType<MiaYourRoll>().Single().Value);
        foreach (var message in emits.OfType<ToAll>().Select(e => e.Message))
            Assert.DoesNotContain("31", message.Serialize());
    }

    [Fact]
    public void NoPublicMessage_EverCarriesTheDiceUnderTheCup()
    {
        var rng = new Random(17);
        for (var trial = 0; trial < 25; trial++)
        {
            var (_, emits) = PlayItOut(2 + trial % 5, lives: 2, seed: trial, rng);

            // Everything the table may know: values a player announced out loud, plus the
            // dice a call turned over. A public message may carry nothing else.
            var spoken = new HashSet<int> { 0 };
            var cup = new Dictionary<string, int>();
            var calls = 0;

            foreach (var emit in emits)
            {
                if (emit is ToPlayer priv)
                {
                    cup[priv.PlayerId] = Assert.IsType<MiaYourRoll>(priv.Message).Value;
                    continue;
                }
                if (emit is not ToAll all) continue;

                Assert.IsNotType<MiaYourRoll>(all.Message);
                var json = all.Message.Serialize();
                Assert.DoesNotContain("yourRoll", json);
                Assert.DoesNotContain("secret", json);

                if (all.Message is MiaAnnounced announced) spoken.Add(announced.Value);
                if (all.Message is MiaCalled called)
                {
                    // The dice shown are the announcer's own, and no others are ever shown.
                    Assert.Equal(cup[called.AnnouncerId], called.Revealed);
                    spoken.Add(called.Revealed);
                    calls++;
                }

                foreach (var value in PublicValues(all.Message))
                    Assert.Contains(value, spoken);
            }

            Assert.Equal(calls, emits.OfType<ToAll>().Count(e => e.Message is MiaCalled));
        }
    }

    // ---------------------------------------------------------------------- bot

    [Fact]
    public void Bots_AlwaysProduceLegalMoves_AndGamesFinish()
    {
        var rng = new Random(5);
        for (var trial = 0; trial < 40; trial++)
        {
            var (module, _) = PlayItOut(2 + trial % 5, lives: 1 + trial % 3, seed: 100 + trial, rng);
            Assert.Equal(GamePhase.GameOver, module.Phase);
            Assert.Null(module.CurrentActorId);
        }
    }

    [Fact]
    public void FacingAMia_TheBotBuysOutOnTwoLives_ButTakesItsChanceOnItsLast()
    {
        var onTwo = Enumerable.Range(0, 200).Select(seed => Answer(lives: 2, seed)).ToList();
        Assert.Contains(onTwo, m => m is MiaConcede);   // a concession cannot knock it out
        Assert.Contains(onTwo, m => m is MiaCallLiar);

        // On one life a concession is just as fatal as a bad call, so it always calls.
        Assert.All(Enumerable.Range(0, 200).Select(seed => Answer(lives: 1, seed)), move => Assert.IsType<MiaCallLiar>(move));

        static NetMessage Answer(int lives, int seed)
        {
            var game = Started(lives, [6, 6]);
            game.Announce("a", MiaValue.Mia);
            return MiaBot.Decide(game, game.Players.First(p => p.Id == "b"), new Random(seed));
        }
    }

    [Fact]
    public void TheBot_AnnouncesMia_WhenNothingElseIsLeft()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var game = Started(3, [6, 6, 3, 1]); // A says 66 honestly, B believes and is cornered
            game.Announce("a", V(66));
            game.Believe("b");

            var move = MiaBot.Decide(game, game.Players.First(p => p.Id == "b"), new Random(seed));

            Assert.Equal(21, Assert.IsType<MiaAnnounce>(move).Value);
        }
    }

    [Fact]
    public void TheBot_UsuallyTellsTheTruthWhenTheTruthIsGoodEnough_AndNeverBluffsMia()
    {
        var truths = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var game = Started(3, [5, 2]); // A holds 52 with nothing to beat
            var move = Assert.IsType<MiaAnnounce>(MiaBot.Decide(game, game.Players.First(p => p.Id == "a"), new Random(seed)));

            Assert.NotEqual(21, move.Value); // it never invents a Mia: that only buys a call
            Assert.True(MiaValue.FromCode(move.Value).RollsAtLeast <= V(52).RollsAtLeast, "a bluff should climb, not drop");
            if (move.Value == 52) truths++;
        }

        Assert.InRange(truths, 140, 200);
    }

    [Fact]
    public void TheBot_ForcedToLie_KeepsItBelievable()
    {
        var picks = new List<int>();
        for (var seed = 0; seed < 200; seed++)
        {
            var game = Started(3, [5, 4, 3, 1]); // A says 54, B believes and then throws 31
            game.Announce("a", V(54));
            game.Believe("b");
            picks.Add(Assert.IsType<MiaAnnounce>(MiaBot.Decide(game, game.Players.First(p => p.Id == "b"), new Random(seed))).Value);
        }

        Assert.All(picks, pick => Assert.True(V(pick).Beats(V(54)), $"{pick} does not beat 54"));
        Assert.DoesNotContain(21, picks);                              // never a bluffed Mia
        Assert.True(picks.Count(p => p == 61) >= 100, "the smallest lie should be the usual one");
    }

    // ------------------------------------------------------------------- wire

    [Fact]
    public void EveryMiaMessage_RoundTripsOnTheWire()
    {
        var table = new MiaTable(3, MiaStep.Respond, "b", "a", 52, 43, [new MiaSeat("a", 3, false), new MiaSeat("b", 1, false)]);
        NetMessage[] samples =
        [
            new MiaAnnounce(52),
            new MiaBelieve(),
            new MiaCallLiar(),
            new MiaConcede(),
            new MiaRoundStarted("a", table),
            new MiaRolled("a", table),
            new MiaYourRoll(21),
            new MiaAnnounced("a", 52, table),
            new MiaBelieved("b", 52, table),
            new MiaCalled("b", "a", 52, 31, false, "a", 1, false, table),
            new MiaConceded("b", "a", 21, false, table),
            new MiaSnapshot(table),
        ];

        foreach (var sample in samples)
        {
            var back = NetMessage.Deserialize(sample.Serialize());
            Assert.IsType(sample.GetType(), back);
            Assert.Equal(sample.Serialize(), back!.Serialize()); // every field survives the trip
        }

        Assert.Equal(samples.Length, GameCatalog.Find(MiaModule.Type)!.Messages.Count);
        Assert.Contains("\"$type\":\"mia.announce\"", new MiaAnnounce(52).Serialize());
    }

    [Fact]
    public void TheCatalog_OffersMia_WithItsLivesOption()
    {
        var mia = GameCatalog.Find(MiaModule.Type)!;

        Assert.Equal(MiaGame.DefaultLives, mia.ResolveOptions(null)["lives"]);
        Assert.Equal(6, mia.ResolveOptions(new Dictionary<string, int> { ["lives"] = 99 })["lives"]);
        Assert.Equal((2, 6), (mia.MinPlayers, mia.MaxPlayers));
        Assert.IsType<MiaModule>(GameCatalog.Create(MiaModule.Type, null));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Plays a whole table of bots through the module, exactly as the room would.</summary>
    private static (MiaModule Module, List<Emit> Emits) PlayItOut(int players, int lives, int seed, Random rng)
    {
        var module = new MiaModule(new RandomDiceRoller(seed), lives);
        for (var i = 0; i < players; i++)
            module.AddPlayer($"p{i}", $"Bot {i}", isBot: true);

        var emits = new List<Emit>(module.Start());
        for (var step = 0; step < 20_000 && module.Phase == GamePhase.Playing; step++)
        {
            var actor = module.CurrentActorId;
            Assert.NotNull(actor);
            var move = module.DecideBotMove(actor!, rng);
            Assert.NotNull(move);
            emits.AddRange(module.Handle(actor!, move!)); // throws if a bot chose something illegal
        }

        Assert.Equal(GamePhase.GameOver, module.Phase);
        return (module, emits);
    }

    /// <summary>Every value a public message puts in front of the table.</summary>
    private static IEnumerable<int> PublicValues(NetMessage message) => message switch
    {
        MiaRoundStarted m => TableValues(m.Table),
        MiaRolled m => TableValues(m.Table),
        MiaAnnounced m => TableValues(m.Table).Append(m.Value),
        MiaBelieved m => TableValues(m.Table).Append(m.Value),
        MiaCalled m => TableValues(m.Table).Append(m.Announced),
        MiaConceded m => TableValues(m.Table).Append(m.Announced),
        MiaSnapshot m => TableValues(m.Table),
        _ => [],
    };

    private static IEnumerable<int> TableValues(MiaTable table) => [table.Announced, table.Accepted];

    private static string Describe(Emit emit) => emit switch
    {
        ToAll { Message: MiaRoundStarted } => "round",
        ToAll { Message: MiaRolled } => "rolled",
        ToAll { Message: MiaAnnounced } => "announced",
        ToAll { Message: MiaBelieved } => "believed",
        ToAll { Message: MiaCalled } => "called",
        ToAll { Message: MiaConceded } => "conceded",
        ToAll { Message: GameEnded } => "over",
        ToPlayer { Message: MiaYourRoll } => "secret",
        Pause { Kind: PauseKind.RoundBreak } => "break",
        Pause => "beat",
        _ => "other",
    };
}
