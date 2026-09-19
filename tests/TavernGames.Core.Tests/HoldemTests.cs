using TavernGames.Core.Cards;
using TavernGames.Core.Games.Holdem;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Tests;

public class HoldemTests
{
    // Seat order is the order players are added, and the button starts on the first seat.
    // Cards go out in two passes beginning to the button's left, so heads-up the deal order
    // is b, a, b, a and three handed it is b, c, a, b, c, a, then the board.
    private static HoldemGame Heads(string deck, int chips = 1000, int smallBlind = 10, int blindsUpEvery = 0)
    {
        var game = new HoldemGame(() => Deck.Stacked(deck.Split(' ')), chips, smallBlind, blindsUpEvery);
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.StartGame();
        return game;
    }

    private static HoldemGame Ring(string deck, int chips = 1000, int smallBlind = 10, int blindsUpEvery = 0)
    {
        var game = new HoldemGame(() => Deck.Stacked(deck.Split(' ')), chips, smallBlind, blindsUpEvery);
        game.AddPlayer("a", "Alice");
        game.AddPlayer("b", "Bob");
        game.AddPlayer("c", "Cid");
        game.StartGame();
        return game;
    }

    private static HoldemPlayer P(HoldemGame game, string id) => game.Players.First(p => p.Id == id);
    private static int Chips(HoldemGame game, string id) => P(game, id).Chips;

    /// <summary>
    /// A stack as it stood at one event. Settling a hand rolls straight into dealing the next
    /// one, so by the time a call returns the blinds for the following hand are already in:
    /// the snapshot on the event is the only place a hand's own result can be read.
    /// </summary>
    private static int Stack(HoldemEvent e, string id) => e.Table.Seats.Single(s => s.PlayerId == id).Chips;

    /// <summary>Anything at all; these tests never reach a showdown.</summary>
    private const string Filler = "2C 3C 4C 5C 6C 7C 8D 9D TD JD QD";

    // -------------------------------------------------------------- blinds and order

    [Fact]
    public void TheBlindsSitToTheButtonsLeft_AndTheSeatAfterThemActsFirst()
    {
        var game = Ring(Filler);

        Assert.Equal("a", game.ButtonId);
        Assert.Equal((1000, 990, 980), (Chips(game, "a"), Chips(game, "b"), Chips(game, "c")));
        Assert.Equal(20, game.CurrentBet);
        Assert.Equal("a", game.CurrentPlayer?.Id); // three handed the button is also first to act
    }

    [Fact]
    public void HeadsUp_TheButtonPostsTheSmallBlind_ActsFirstPreflop_AndLastAfterIt()
    {
        var game = Heads(Filler);

        Assert.Equal((990, 980), (Chips(game, "a"), Chips(game, "b")));
        Assert.Equal("a", game.CurrentPlayer?.Id);

        game.Act("a", HoldemMove.Call);
        game.Act("b", HoldemMove.Check);

        Assert.Equal(HoldemStreet.Flop, game.Street);
        Assert.Equal("b", game.CurrentPlayer?.Id); // the button is last on every later street
    }

    [Fact]
    public void TheBigBlind_GetsTheOptionToRaise_WhenNobodyRaisedBeforeIt()
    {
        var game = Ring(Filler);
        game.Act("a", HoldemMove.Call);
        game.Act("b", HoldemMove.Call);

        // Everyone has matched, but the big blind has not had a say yet.
        Assert.Equal(HoldemStreet.Preflop, game.Street);
        Assert.Equal("c", game.CurrentPlayer?.Id);

        game.Act("c", HoldemMove.Raise, 80);

        Assert.Equal("a", game.CurrentPlayer?.Id);
        Assert.Equal(80, game.CurrentBet);
    }

    [Fact]
    public void ABettingRoundEnds_OnceEveryoneHasActedAndMatchedTheBet()
    {
        var game = Ring(Filler);
        game.Act("a", HoldemMove.Call);
        game.Act("b", HoldemMove.Call);
        var events = game.Act("c", HoldemMove.Check);

        var dealt = Assert.Single(events, e => e.Kind == HoldemEventKind.StreetDealt);
        Assert.Equal(HoldemStreet.Flop, dealt.Street);
        Assert.Equal(3, dealt.Cards!.Length);
        Assert.Equal("b", game.CurrentPlayer?.Id); // first live seat left of the button
        Assert.Equal(0, game.CurrentBet);
    }

    [Fact]
    public void TheButton_MovesOneLiveSeatEachHand()
    {
        var game = Ring(Filler);
        Assert.Equal("a", game.ButtonId);

        game.Act("a", HoldemMove.Fold);
        game.Act("b", HoldemMove.Fold); // c takes it down, next hand starts in the same chain

        Assert.Equal("b", game.ButtonId);
        Assert.Equal(2, game.Hand);
    }

    // ---------------------------------------------------------------- raise sizing

    [Fact]
    public void TheMinimumRaise_IsTheSizeOfThePreviousRaiseOnThatStreet()
    {
        var game = Ring(Filler);

        Assert.Equal(40, game.MinRaiseTo); // twice the big blind to open
        game.Act("a", HoldemMove.Raise, 60);
        Assert.Equal(100, game.MinRaiseTo); // a raise of 40 has to be raised by 40 more

        Assert.Throws<InvalidOperationException>(() => game.Act("b", HoldemMove.Raise, 90));
        game.Act("b", HoldemMove.Raise, 100);
        Assert.Equal(100, game.CurrentBet);
    }

    [Fact]
    public void ARaise_MustBeatTheCurrentBet_AndFitInTheStack()
    {
        var game = Ring(Filler, chips: 200);

        Assert.Throws<InvalidOperationException>(() => game.Act("a", HoldemMove.Raise, 20));  // not a raise
        Assert.Throws<InvalidOperationException>(() => game.Act("a", HoldemMove.Raise, 201)); // more than we have
        game.Act("a", HoldemMove.Raise, 200);                                                // all-in is fine
        Assert.Equal(0, Chips(game, "a"));
    }

    [Fact]
    public void IllegalActions_AreRefusedWithAMessage()
    {
        var game = Ring(Filler);

        Assert.Throws<InvalidOperationException>(() => game.Act("b", HoldemMove.Call));   // not b's turn
        Assert.Throws<InvalidOperationException>(() => game.Act("a", HoldemMove.Check));  // 20 to call
        game.Act("a", HoldemMove.Call);
        game.Act("b", HoldemMove.Call);
        Assert.Throws<InvalidOperationException>(() => game.Act("c", HoldemMove.Call));   // nothing to call
        Assert.Throws<InvalidOperationException>(() => game.Act("nobody", HoldemMove.Check));
    }

    /// <summary>
    /// Stacks only differ once a hand has been played, so this plays one out: Alice folds
    /// her button, Bob raises, Cid calls and then gives up on the river. Bob 400, Cid 200,
    /// Alice 300, and Bob has the button for the next hand.
    /// </summary>
    private static HoldemGame UnevenTable(string deck)
    {
        var game = Ring(deck, chips: 300);
        game.Act("a", HoldemMove.Fold);
        game.Act("b", HoldemMove.Raise, 100);
        game.Act("c", HoldemMove.Call);
        game.Act("b", HoldemMove.Check);
        game.Act("c", HoldemMove.Check); // the flop is checked through
        game.Act("b", HoldemMove.Check);
        game.Act("c", HoldemMove.Check); // and so is the turn
        game.Act("b", HoldemMove.Raise, 150);
        var events = game.Act("c", HoldemMove.Fold);

        var ended = events.First(e => e.Kind == HoldemEventKind.HandEnded);
        Assert.Equal((300, 400, 200), (Stack(ended, "a"), Stack(ended, "b"), Stack(ended, "c")));

        // Hand two is already dealt: Bob has the button, Cid is the small blind, Alice the big.
        Assert.Equal("b", game.ButtonId);
        Assert.Equal((280, 400, 190), (Chips(game, "a"), Chips(game, "b"), Chips(game, "c")));
        return game;
    }

    [Fact]
    public void AnAllInShortOfAFullRaise_DoesNotReopenTheBetting_ButAFullRaiseDoes()
    {
        var game = UnevenTable(Filler);
        // Hand two: Bob on the button with 400, Cid the small blind with 200, Alice the big blind with 300.
        game.Act("b", HoldemMove.Raise, 115); // a raise of 95 over the big blind

        // Cid cannot reach 210, so all-in for 200 is a raise of only 85.
        Assert.Equal(210, game.MinRaiseTo);
        game.Act("c", HoldemMove.Raise, 200);
        Assert.Equal(200, game.CurrentBet);

        // Bob already acted, so the short all-in leaves him calling or folding.
        Assert.False(game.CanRaise(P(game, "b")));
        // Alice has not acted yet, so she keeps the full right to raise.
        Assert.True(game.CanRaise(P(game, "a")));

        // A full raise of 95 on top. Alice keeps five chips back rather than shoving, because
        // a table where every opponent is all-in has no raise left in it to reopen.
        game.Act("a", HoldemMove.Raise, 295);
        Assert.False(P(game, "a").AllIn);

        // A full raise reopens the betting for everyone, Bob included.
        Assert.True(game.CanRaise(P(game, "b")));
        Assert.Equal("b", game.CurrentPlayer?.Id);
    }

    [Fact]
    public void WithEveryOpponentAllIn_ThereIsNoRaiseLeft_HoweverMuchIsBehind()
    {
        var game = UnevenTable(Filler);
        // Hand two: Bob on the button with 400, Cid the small blind with 200, Alice the big blind with 300.
        game.Act("b", HoldemMove.Call);       // 20
        game.Act("c", HoldemMove.Raise, 200); // Cid shoves: a full raise, so Bob is live again
        game.Act("a", HoldemMove.Raise, 300); // Alice shoves too, short of the 380 minimum

        // Bob is back on the clock with 380 behind and nobody left who could put in another
        // chip. A raise needs someone able to call it, so his only moves are call and fold.
        Assert.Equal("b", game.CurrentPlayer?.Id);
        Assert.False(game.CanRaise(P(game, "b")));
        Assert.False(game.Snapshot().CanRaise);

        var refused = Assert.Throws<InvalidOperationException>(() => game.Act("b", HoldemMove.Raise, 400));
        Assert.Contains("call or fold", refused.Message);

        // Calling is still fine, and closes the betting for good.
        var events = game.Act("b", HoldemMove.Call);
        Assert.Contains(events, e => e.Kind == HoldemEventKind.HandEnded);
    }

    [Fact]
    public void AfterAShortAllIn_AnAlreadyActedSeat_MayOnlyCallOrFold()
    {
        // Heads-up so the short all-in comes straight back to the seat it closed the action on.
        var game = Heads(Filler, chips: 200);
        game.Act("a", HoldemMove.Fold); // Bob takes the blinds: Alice 190, Bob 210

        game.Act("b", HoldemMove.Raise, 150); // Bob on the button, a raise of 130
        game.Act("a", HoldemMove.Raise, 190); // all-in for 40 more, short of the 280 minimum

        Assert.False(game.CanRaise(P(game, "b")));
        var refused = Assert.Throws<InvalidOperationException>(() => game.Act("b", HoldemMove.Raise, 210));
        Assert.Contains("call or fold", refused.Message);

        var events = game.Act("b", HoldemMove.Call);
        // Nothing left to bet, so the hands go up and the board is dealt out in one chain.
        Assert.Contains(events, e => e.Kind == HoldemEventKind.Showdown);
        Assert.Equal(3, events.Count(e => e.Kind == HoldemEventKind.StreetDealt));
    }

    // ------------------------------------------------------------------- payouts

    [Fact]
    public void EveryoneFolding_TakesThePotWithoutShowingACard_AndTheUncalledBetComesBack()
    {
        var game = Heads(Filler);
        game.Act("a", HoldemMove.Raise, 200);

        var events = game.Act("b", HoldemMove.Fold);

        var ended = Assert.Single(events, e => e.Kind == HoldemEventKind.HandEnded);
        Assert.DoesNotContain(events, e => e.Kind == HoldemEventKind.Showdown);

        // 180 of the 200 was never matched, so it is returned rather than "won".
        Assert.Equal(
            [(HoldemAwardKind.Uncalled, 180), (HoldemAwardKind.Main, 40)],
            ended.Awards!.Select(x => (x.Kind, x.Amount)).ToArray());
        Assert.Equal((1020, 980), (Stack(ended, "a"), Stack(ended, "b")));

        // Neither hand was shown, so neither is in the public table.
        var seats = ended.Table.Seats;
        Assert.All(seats, s => Assert.All(s.Cards, c => Assert.Equal(Card.HiddenCode, c)));
    }

    [Fact]
    public void AllInPlayers_RunTheBoardOut_AndSidePotsPayWhatEachSeatCovered()
    {
        // Cid is short with aces, Alice is busted with nothing, Bob has the biggest stack and kings.
        var game = UnevenTable("AS 2C KS AD 7H KH 3D 4S 9C JH QS");

        game.Act("b", HoldemMove.Raise, 400); // all-in over the top
        game.Act("c", HoldemMove.Call);       // all-in for 200
        var events = game.Act("a", HoldemMove.Call); // all-in for 300

        // No betting left, so the hands go up and the board is dealt one street at a time.
        var kinds = events.Select(e => e.Kind).ToList();
        Assert.Equal(
        [
            HoldemEventKind.Acted,
            HoldemEventKind.Showdown,
            HoldemEventKind.StreetDealt, HoldemEventKind.StreetDealt, HoldemEventKind.StreetDealt,
            HoldemEventKind.Showdown,
            HoldemEventKind.HandEnded,
            // Settling rolls straight into dealing the next hand.
            HoldemEventKind.HandStarted, HoldemEventKind.BlindPosted, HoldemEventKind.BlindPosted,
        ], kinds);

        var ended = events.First(e => e.Kind == HoldemEventKind.HandEnded);
        Assert.Equal(
            [
                (HoldemAwardKind.Uncalled, "b", 100), // Bob's 100 nobody could cover
                (HoldemAwardKind.Main, "c", 600),     // aces take the pot all three contested
                (HoldemAwardKind.Side, "b", 200),     // kings take what only Bob and Alice covered
            ],
            ended.Awards!.Select(x => (x.Kind, x.PlayerId, x.Amount)).ToArray());

        Assert.Equal(["a"], ended.BustedOut!);
        Assert.True(P(game, "a").Eliminated);
        Assert.Equal((0, 300, 600), (Stack(ended, "a"), Stack(ended, "b"), Stack(ended, "c")));
    }

    [Fact]
    public void TheShowdown_DescribesEveryShownHand_AndLeavesFoldedOnesFaceDown()
    {
        var game = UnevenTable("AS 2C KS AD 7H KH 3D 4S 9C JH QS");
        game.Act("b", HoldemMove.Raise, 400);
        game.Act("c", HoldemMove.Call);
        var events = game.Act("a", HoldemMove.Call);

        var final = events.Last(e => e.Kind == HoldemEventKind.Showdown);
        Assert.Equal(
            [("a", "Queen high"), ("b", "Pair of Kings"), ("c", "Pair of Aces")],
            final.Reveals!.OrderBy(r => r.PlayerId).Select(r => (r.PlayerId, r.Hand)).ToArray());

        // The first reveal turns the cards up but says nothing about them: the board is not out yet.
        var early = events.First(e => e.Kind == HoldemEventKind.Showdown);
        Assert.All(early.Reveals!, r => Assert.Null(r.Hand));
        Assert.Empty(early.Table.Community);
    }

    [Fact]
    public void ASplitPot_DividesEvenly_AndTheOddChipGoesToTheSeatNearestTheButtonsLeft()
    {
        // A royal flush on the board: Alice and Cid both play it and split what Bob left behind.
        var game = Ring("2C 3C 4C 5C 6C 7C AS KS QS JS TS", smallBlind: 5);
        game.Act("a", HoldemMove.Call);
        game.Act("b", HoldemMove.Fold);
        game.Act("c", HoldemMove.Check);
        game.Act("c", HoldemMove.Check);
        game.Act("a", HoldemMove.Check); // flop
        game.Act("c", HoldemMove.Check);
        game.Act("a", HoldemMove.Check); // turn
        game.Act("c", HoldemMove.Check);
        var events = game.Act("a", HoldemMove.Check); // river, then showdown

        var ended = events.First(e => e.Kind == HoldemEventKind.HandEnded);
        // 25 chips, two winners: Cid sits nearer the button's left, so the odd chip is his.
        Assert.Equal(
            [("c", 13), ("a", 12)],
            ended.Awards!.Select(x => (x.PlayerId, x.Amount)).ToArray());
        Assert.Equal((1002, 995, 1003), (Stack(ended, "a"), Stack(ended, "b"), Stack(ended, "c")));
    }

    [Fact]
    public void ASeatThatCannotCoverItsBlind_PostsWhatItHas_AndTheFullBlindIsStillTheBet()
    {
        var game = Heads("AS 2D KS 3D 7H 8C 9S JD QH", chips: 300, smallBlind: 100, blindsUpEvery: 1);
        game.Act("a", HoldemMove.Fold); // Bob takes it down: Alice 200, Bob 400

        // Hand two, blinds doubled: Alice owes 400 as the big blind but only has 200.
        Assert.Equal((2, 200, 400), (game.BlindLevel, game.SmallBlind, game.BigBlind));
        Assert.Equal(0, Chips(game, "a"));
        Assert.True(P(game, "a").AllIn);
        Assert.Equal(200, P(game, "a").StreetBet);
        Assert.Equal(400, game.CurrentBet); // the short blind does not lower the price
        Assert.Equal("b", game.CurrentPlayer?.Id);
        Assert.False(game.CanRaise(P(game, "b"))); // and there is nobody left to raise at

        var events = game.Act("b", HoldemMove.Call);
        var ended = events.First(e => e.Kind == HoldemEventKind.HandEnded);
        // Bob could only be called for 200, so the other 200 comes straight back.
        Assert.Contains(ended.Awards!, x => x is { Kind: HoldemAwardKind.Uncalled, PlayerId: "b", Amount: 200 });
    }

    [Fact]
    public void LosingEveryChip_Eliminates_AndTheLastStackStandingWins()
    {
        var game = Heads("2C AS 3D AD 7H 8S 9C JD QH", chips: 100);
        game.Act("a", HoldemMove.Raise, 100);
        game.Act("b", HoldemMove.Call);

        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal("a", game.WinnerId);
        Assert.Equal((200, 0), (Chips(game, "a"), Chips(game, "b")));
        Assert.True(P(game, "b").Eliminated);
    }

    [Fact]
    public void TheBlinds_DoubleOnScheduleAndThenStopClimbing()
    {
        var game = Ring(Filler, chips: 600, smallBlind: 10, blindsUpEvery: 2);

        Assert.Equal((1, 10, 2), (game.BlindLevel, game.SmallBlind, game.HandsUntilBlindsUp));
        game.Act("a", HoldemMove.Fold);
        game.Act("b", HoldemMove.Fold);
        Assert.Equal((2, 1, 10), (game.Hand, game.BlindLevel, game.SmallBlind)); // still level one
        game.Act("b", HoldemMove.Fold);
        game.Act("c", HoldemMove.Fold);
        Assert.Equal((3, 2, 20), (game.Hand, game.BlindLevel, game.SmallBlind));

        var never = Ring(Filler, blindsUpEvery: 0);
        Assert.Equal((1, 0), (never.BlindLevel, never.HandsUntilBlindsUp));
    }

    // ------------------------------------------------------------------- leaving

    [Fact]
    public void LeavingOnYourTurn_PassesTheActionOn()
    {
        var game = Ring(Filler);
        Assert.Equal("a", game.CurrentPlayer?.Id);

        game.RemovePlayer("a");

        Assert.Equal("b", game.CurrentPlayer?.Id);
        Assert.Equal(GamePhase.Playing, game.Phase);
        Assert.Equal(2, game.Players.Count);
    }

    [Fact]
    public void LeavingWhenItIsNotYourTurn_DisturbsNothing_ButTheChipsStayInThePot()
    {
        var game = Ring(Filler);
        game.Act("a", HoldemMove.Call);
        game.Act("b", HoldemMove.Call);
        Assert.Equal("c", game.CurrentPlayer?.Id);

        game.RemovePlayer("b"); // Bob had 20 in the middle

        Assert.Equal("c", game.CurrentPlayer?.Id);
        Assert.Equal(60, game.Pot); // Alice 20, Cid 20, and Bob's abandoned 20
        Assert.Equal(GamePhase.Playing, game.Phase);
    }

    [Fact]
    public void TheLastOpponentLeaving_EndsTheHandAndTheGame()
    {
        var game = Heads(Filler);

        var events = game.RemovePlayer("b");

        Assert.Contains(events, e => e.Kind == HoldemEventKind.HandEnded);
        Assert.Equal(GamePhase.GameOver, game.Phase);
        Assert.Equal("a", game.WinnerId);
        // Her own 10 back, plus the 10 of Bob's blind she had matched. The other half of his
        // blind was never called, so it goes out of the door with him.
        Assert.Equal(1010, Chips(game, "a"));
    }

    [Fact]
    public void TheButtonLeavingMidHand_LeavesTheMarkerOnASeat_AndChangesNoOrder()
    {
        var game = Ring(Filler);
        Assert.Equal("a", game.ButtonId);
        game.Act("a", HoldemMove.Call);
        game.Act("b", HoldemMove.Call);
        game.Act("c", HoldemMove.Check); // the flop is out and Bob is first to act

        game.RemovePlayer("a"); // the seat holding the button walks out

        // The button is a dead button on the empty chair, which is the seat before Bob either
        // way, so the table still has a marker to draw and the order is untouched.
        Assert.Equal("c", game.ButtonId);
        Assert.Equal("b", game.CurrentPlayer?.Id);

        game.Act("b", HoldemMove.Check);
        game.Act("c", HoldemMove.Check);
        game.Act("b", HoldemMove.Check);
        game.Act("c", HoldemMove.Check);
        game.Act("b", HoldemMove.Check);
        game.Act("c", HoldemMove.Check); // checked down to a showdown, and the next hand deals

        Assert.Equal(2, game.Hand);
        Assert.Equal("b", game.ButtonId); // the button moves on exactly as it would have
    }

    [Fact]
    public void AShoveFromASeatThatThenLeaves_StaysInThePotOnlyAsFarAsItWasMatched()
    {
        var game = UnevenTable(Filler);
        // Hand two: Bob on the button with 400, Cid the small blind (10 in), Alice the big blind (20 in).
        game.Act("b", HoldemMove.Raise, 400); // Bob shoves
        game.RemovePlayer("b");               // and closes the window before anyone can answer

        // Only the 20 Alice had already put up ever covered any of it. An uncalled bet is
        // refunded whoever made it, so the rest leaves the game on Bob's way out rather than
        // swelling a pot he cannot win.
        Assert.Equal(50, game.Pot); // Alice's 20, Cid's 10 and the 20 of Bob's that was matched

        var events = game.Act("c", HoldemMove.Fold);

        var ended = events.First(e => e.Kind == HoldemEventKind.HandEnded);
        Assert.Equal([("a", 50)], ended.Awards!.Select(x => (x.PlayerId, x.Amount)).ToArray());
        Assert.Equal(330, Stack(ended, "a"));
    }

    [Fact]
    public void LeavingCanCloseTheBettingRound_WithoutStallingTheTable()
    {
        var game = Ring(Filler);
        game.Act("a", HoldemMove.Call);
        game.Act("b", HoldemMove.Call);

        // Only Cid still owed an action; with him gone the street has to move on by itself.
        var events = game.RemovePlayer("c");

        Assert.Contains(events, e => e.Kind == HoldemEventKind.StreetDealt);
        Assert.Equal(HoldemStreet.Flop, game.Street);
        Assert.Equal("b", game.CurrentPlayer?.Id);
    }

    // -------------------------------------------------------------------- module

    [Fact]
    public void TheModule_DealsEachSeatItsOwnCards_AndTellsTheTableNothing()
    {
        var module = Module(Filler);

        var opening = module.Start();

        var deals = opening.OfType<ToPlayer>().ToList();
        Assert.Equal(2, deals.Count);
        Assert.All(deals, d => Assert.IsType<HoldemYourCards>(d.Message));
        Assert.Equal(["a", "b"], deals.Select(d => d.PlayerId).Order().ToArray());

        var started = Assert.IsType<HoldemHandStarted>(Assert.IsType<ToAll>(opening[0]).Message);
        Assert.All(started.Table.Seats, s => Assert.Equal([Card.HiddenCode, Card.HiddenCode], s.Cards));
        Assert.Equal(2, opening.OfType<ToAll>().Count(e => e.Message is HoldemBlindPosted));
    }

    [Fact]
    public void TheModule_BreathesBetweenStreets_AndGivesTimeToReadTheResult()
    {
        var module = Module("2C AS 3D AD 7H 8S 9C JD QH", chips: 100);
        module.Start();
        module.Handle("a", new HoldemAct(HoldemMove.Raise, 100));

        var beats = module.Handle("b", new HoldemAct(HoldemMove.Call)).Select(e => e switch
        {
            ToAll { Message: HoldemActed } => "acted",
            ToAll { Message: HoldemShowdown } => "showdown",
            ToAll { Message: HoldemStreetDealt } => "street",
            ToAll { Message: HoldemHandEnded } => "ended",
            ToAll { Message: GameEnded } => "over",
            Pause { Kind: PauseKind.Beat } => "beat",
            Pause { Kind: PauseKind.RoundBreak } => "break",
            _ => "other",
        }).ToList();

        Assert.Equal(
        [
            "acted",
            "beat", "showdown",
            "beat", "street", "beat", "street", "beat", "street",
            "beat", "showdown",
            "ended", "break",
            "over",
        ], beats);
    }

    [Fact]
    public void TheModule_RedrawsTheTable_WhenSomeoneLeavesWithoutChangingTheGame()
    {
        var module = new HoldemModule(() => Deck.Stacked(Filler.Split(' ')), 1000, 10, 0);
        module.AddPlayer("a", "Alice", isBot: false);
        module.AddPlayer("b", "Bob", isBot: false);
        module.AddPlayer("c", "Cid", isBot: false);
        module.Start();
        module.Handle("a", new HoldemAct(HoldemMove.Call));
        module.Handle("b", new HoldemAct(HoldemMove.Call));

        var emits = module.RemovePlayer("a"); // not on the clock, hand carries on

        var snapshot = Assert.IsType<HoldemSnapshot>(Assert.IsType<ToAll>(Assert.Single(emits)).Message);
        Assert.Equal(["b", "c"], snapshot.Table.Seats.Select(s => s.PlayerId).ToArray());
        Assert.Equal("c", module.CurrentActorId);
    }

    [Fact]
    public void TheModule_RejectsMovesFromOtherGames()
    {
        var module = Module(Filler);
        module.Start();
        Assert.Throws<InvalidOperationException>(() => module.Handle("a", new Games.Pig.PigRoll()));
    }

    [Fact]
    public void CatchUp_HandsALateWatcherTheCurrentPublicStateAndNothingElse()
    {
        var module = Module("2C AS 3D AD 7H 8S 9C JD QH");
        module.Start();
        module.Handle("a", new HoldemAct(HoldemMove.Call));
        module.Handle("b", new HoldemAct(HoldemMove.Check)); // flop is out

        var catchUp = module.CatchUp();

        var table = Assert.IsType<HoldemSnapshot>(Assert.Single(catchUp)).Table;
        Assert.Equal(HoldemStreet.Flop, table.Street);
        Assert.Equal(["7H", "8S", "9C"], table.Community);
        Assert.Equal(40, table.Pot);
        Assert.Equal("b", table.CurrentPlayerId);
        Assert.Equal("a", table.ButtonId);
        Assert.All(table.Seats, s => Assert.All(s.Cards, c => Assert.Equal(Card.HiddenCode, c)));
    }

    // ----------------------------------------------------------------------- bots

    [Fact]
    public void Bots_AlwaysProduceLegalMoves_EveryGameFinishes_AndTheChipsAlwaysAddUp()
    {
        var rng = new Random(9);
        for (var trial = 0; trial < 40; trial++)
        {
            var startingChips = 500 + trial * 50;
            var seats = 2 + trial % (HoldemGame.MaxPlayers - 1);
            var module = new HoldemModule(
                Deck.Shuffled,
                startingChips,
                smallBlind: 10,
                blindsUpEvery: trial % 4 == 0 ? 0 : 2 + trial % 5);

            for (var i = 0; i < seats; i++)
                module.AddPlayer($"bot{i}", $"Bot {i}", isBot: true);

            // Refunds, side pots and odd chips all move money by hand, so the bank is audited
            // on every public table: no chip is ever minted or burned, and a hand pays out
            // exactly what was pushed into it, no more and none left behind.
            var bank = startingChips * seats;
            var stakedBeforeSettling = 0;

            void Audit(IReadOnlyList<Emit> emits)
            {
                foreach (var message in emits.OfType<ToAll>().Select(e => e.Message))
                {
                    if (message is HoldemHandEnded ended)
                        Assert.Equal(stakedBeforeSettling, ended.Awards.Sum(a => a.Amount));

                    if (TableOf(message) is not { } table) continue;
                    Assert.Equal(bank, table.Seats.Sum(s => s.Chips) + table.Pot);
                    stakedBeforeSettling = table.Pot;
                }
            }

            Audit(module.Start());

            for (var step = 0; step < 100_000 && module.Phase == GamePhase.Playing; step++)
            {
                var actor = module.CurrentActorId;
                Assert.NotNull(actor);
                var move = module.DecideBotMove(actor!, rng);
                Assert.NotNull(move);
                Audit(module.Handle(actor!, move!)); // throws if the bot chose something illegal
            }

            Assert.Equal(GamePhase.GameOver, module.Phase);
        }
    }

    [Fact]
    public void Bots_NeverFoldWhenCheckingIsFree_AndNeverRaiseWhenTheyMayNot()
    {
        var rng = new Random(3);
        var deckRng = new Random(3); // seeded deal so this statistical check is deterministic
        var module = new HoldemModule(() => Deck.Shuffled(deckRng), 1000, 10, 0);
        for (var i = 0; i < 4; i++) module.AddPlayer($"bot{i}", $"Bot {i}", isBot: true);

        // The public table says what the seat on the clock is allowed to do, so the decision
        // can be judged from outside the engine, exactly as a client would judge it.
        var table = Assert.IsType<HoldemHandStarted>(
            module.Start().OfType<ToAll>().Select(e => e.Message).First(m => m is HoldemHandStarted)).Table;

        var freeChecks = 0;
        for (var step = 0; step < 20_000 && module.Phase == GamePhase.Playing; step++)
        {
            var actor = module.CurrentActorId!;
            Assert.Equal(actor, table.CurrentPlayerId);
            var move = (HoldemAct)module.DecideBotMove(actor, rng)!;

            if (table.ToCall == 0)
            {
                Assert.NotEqual(HoldemMove.Fold, move.Move);
                Assert.NotEqual(HoldemMove.Call, move.Move);
                freeChecks++;
            }
            if (move.Move == HoldemMove.Raise)
            {
                Assert.True(table.CanRaise);
                Assert.InRange(move.Amount, table.MinRaiseTo, table.MaxRaiseTo);
            }

            var emits = module.Handle(actor, move);
            if (module.Phase != GamePhase.Playing) break;
            table = emits.OfType<ToAll>().Select(e => TableOf(e.Message)).OfType<HoldemTable>().Last();
        }

        Assert.True(freeChecks > 20, "the bots should have faced plenty of free cards over a whole game");
    }

    [Theory]
    [InlineData("AS", "AH", 20)] // the best hand there is
    [InlineData("AS", "KS", 12)] // big, suited and connected
    [InlineData("7D", "2C", -1)] // the worst
    public void TheBotsStartingHandScore_RanksHandsTheWayAPlayerWould(string one, string two, int expected) =>
        Assert.Equal(expected, HoldemBot.ChenScore(Card.Parse(one), Card.Parse(two)));

    // ------------------------------------------------------------- hidden information

    [Fact]
    public void NoUnshownHoleCard_EverReachesAPublicMessage()
    {
        var rng = new Random(21);
        var evictions = 0;
        for (var trial = 0; trial < 8; trial++)
        {
            var module = new HoldemModule(Deck.Shuffled, 400, 10, 3);
            var seats = 2 + trial % (HoldemGame.MaxPlayers - 1);
            for (var i = 0; i < seats; i++) module.AddPlayer($"p{i}", $"Seat {i}", isBot: true);

            var hole = new Dictionary<string, string[]>();
            var shown = new HashSet<string>();

            void Inspect(IReadOnlyList<Emit> emits)
            {
                foreach (var emit in emits)
                {
                    if (emit is ToPlayer { Message: HoldemYourCards cards } deal)
                    {
                        hole[deal.PlayerId] = cards.Cards;
                        continue;
                    }
                    if (emit is not ToAll all) continue;

                    if (all.Message is HoldemHandStarted)
                    {
                        hole.Clear(); // fresh cards are about to be dealt privately
                        shown.Clear();
                    }

                    // A showdown is the moment hands become public, so the seats it names are
                    // allowed to be face up in the very message that turns them over.
                    if (all.Message is HoldemShowdown reveal)
                        foreach (var r in reveal.Reveals)
                            shown.Add(r.PlayerId);

                    var table = TableOf(all.Message);
                    if (table is null) continue; // the shared game-over message carries no table

                    // Structural: a seat nobody has seen shows backs, or nothing at all.
                    foreach (var seat in table.Seats.Where(s => !shown.Contains(s.PlayerId)))
                        Assert.All(seat.Cards, c => Assert.Equal(Card.HiddenCode, c));

                    // And the codes must not appear anywhere else in the message either.
                    var json = all.Message.Serialize();
                    foreach (var (id, codes) in hole.Where(kv => !shown.Contains(kv.Key)))
                        foreach (var code in codes)
                            Assert.DoesNotContain(code, json, StringComparison.Ordinal);
                }
            }

            Inspect(module.Start());
            for (var step = 0; step < 100_000 && module.Phase == GamePhase.Playing; step++)
            {
                // A late watcher's catch-up is a public message like any other, and so is the
                // redraw a seat leaving triggers. Both are built from the same snapshot, so
                // both go through the same scan rather than being taken on trust.
                Inspect([.. module.CatchUp().Select(m => (Emit)new ToAll(m))]);

                // Half the tables also lose a seat mid-hand, which is the other way a snapshot
                // reaches the room without anybody having acted.
                if (trial % 2 == 1 && step > 0 && step % 17 == 0 && module.Roster().Length > 2)
                {
                    evictions++;
                    Inspect(module.RemovePlayer(module.Roster()[^1].Id));
                    if (module.Phase != GamePhase.Playing) break;
                }

                var actor = module.CurrentActorId!;
                Inspect(module.Handle(actor, module.DecideBotMove(actor, rng)!));
            }
            Assert.Equal(GamePhase.GameOver, module.Phase);
        }

        Assert.True(evictions > 0, "the scan should have covered seats walking out mid-hand");
    }

    [Fact]
    public void AFoldedHand_IsNeverShown_EvenWhenTheHandGoesToAShowdown()
    {
        // Cid folds before the flop; Alice and Bob play on and turn their cards over.
        // Deal order is b, c, a twice over, so Cid holds the ace and the seven of hearts.
        var game = Ring("2C AS 3D AD 7H KH 8S 9C TC JD QH", chips: 200);
        game.Act("a", HoldemMove.Raise, 200);
        game.Act("b", HoldemMove.Call);
        var events = game.Act("c", HoldemMove.Fold);

        var showdown = events.Last(e => e.Kind == HoldemEventKind.Showdown);
        Assert.Equal(["a", "b"], showdown.Reveals!.Select(r => r.PlayerId).Order().ToArray());
        Assert.Empty(showdown.Table.Seats.Single(s => s.PlayerId == "c").Cards);

        var json = new HoldemShowdown(showdown.Reveals!, showdown.Table).Serialize();
        Assert.DoesNotContain("AS", json, StringComparison.Ordinal);
        Assert.DoesNotContain("7H", json, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- the wire

    [Fact]
    public void EveryHoldemMessage_RoundTripsOnTheWire()
    {
        var module = Module(Filler);
        module.Start();
        var table = Assert.IsType<HoldemSnapshot>(module.CatchUp().Single()).Table;

        NetMessage[] samples =
        [
            new HoldemAct(HoldemMove.Raise, 120),
            new HoldemHandStarted(table),
            new HoldemYourCards(["AS", "KD"]),
            new HoldemBlindPosted("a", 10, false, table),
            new HoldemActed("a", HoldemMove.Call, 20, table),
            new HoldemStreetDealt(HoldemStreet.Flop, ["2C", "3D", "4H"], table),
            new HoldemShowdown([new HoldemReveal("a", ["AS", "KD"], "Pair of Aces")], table),
            new HoldemHandEnded([new HoldemAward("a", 40, HoldemAwardKind.Main, "Pair of Aces")], ["b"], table),
            new HoldemSnapshot(table),
        ];

        Assert.Equal(HoldemModule.Descriptor.Messages.Count, samples.Length);
        foreach (var sample in samples)
            Assert.IsType(sample.GetType(), NetMessage.Deserialize(sample.Serialize()));

        Assert.Contains("\"$type\":\"holdem.act\"", new HoldemAct(HoldemMove.Fold).Serialize());

        // The table survives the trip intact, which is what every client actually renders.
        var back = Assert.IsType<HoldemSnapshot>(NetMessage.Deserialize(new HoldemSnapshot(table).Serialize()));
        Assert.Equal(
            (table.Hand, table.BigBlind, table.Pot, table.CurrentPlayerId, table.MinRaiseTo, table.Seats.Length),
            (back.Table.Hand, back.Table.BigBlind, back.Table.Pot, back.Table.CurrentPlayerId,
                back.Table.MinRaiseTo, back.Table.Seats.Length));
    }

    [Fact]
    public void TheDescriptor_IsRegistered_AndClampsItsOptions()
    {
        var descriptor = GameCatalog.Find(HoldemModule.Type);
        Assert.NotNull(descriptor);

        var defaults = descriptor!.ResolveOptions(null);
        Assert.Equal((1000, 10, 8), (defaults["startingChips"], defaults["smallBlind"], defaults["blindsUpEvery"]));

        var clamped = descriptor.ResolveOptions(new Dictionary<string, int>
        {
            ["startingChips"] = 99_999,
            ["smallBlind"] = -5,
            ["blindsUpEvery"] = 400,
        });
        Assert.Equal((10_000, 5, 20), (clamped["startingChips"], clamped["smallBlind"], clamped["blindsUpEvery"]));

        var module = Assert.IsType<HoldemModule>(GameCatalog.Create(HoldemModule.Type, null));
        Assert.Equal((2, 6), (module.MinPlayers, module.MaxPlayers));
    }

    // ------------------------------------------------------------------- helpers

    private static HoldemModule Module(string deck, int chips = 1000, int smallBlind = 10, int blindsUpEvery = 0)
    {
        var module = new HoldemModule(() => Deck.Stacked(deck.Split(' ')), chips, smallBlind, blindsUpEvery);
        module.AddPlayer("a", "Alice", isBot: false);
        module.AddPlayer("b", "Bob", isBot: false);
        return module;
    }

    private static HoldemTable? TableOf(NetMessage message) => message switch
    {
        HoldemHandStarted m => m.Table,
        HoldemBlindPosted m => m.Table,
        HoldemActed m => m.Table,
        HoldemStreetDealt m => m.Table,
        HoldemShowdown m => m.Table,
        HoldemHandEnded m => m.Table,
        HoldemSnapshot m => m.Table,
        _ => null,
    };
}
