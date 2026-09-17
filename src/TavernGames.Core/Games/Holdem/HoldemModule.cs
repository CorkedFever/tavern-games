using TavernGames.Core.Cards;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Holdem;

/// <summary>
/// A tight-ish, price-aware opponent. Correctness comes first: every decision is put
/// through <see cref="Legalize"/>, so the bot cannot produce an illegal move however odd
/// its reasoning gets, and it never folds when checking is free.
///
/// Before the flop it tiers its two cards with the Chen formula (pairs, high cards,
/// suitedness, connectedness) and shades that by how many seats still act behind it. After
/// the flop it turns its made hand into a rough equity, adds something for a flush or open
/// ended straight draw, and compares that with the price the pot is offering. A little of
/// the rng goes into occasional bluffs and slow-plays so a raise is never a tell.
/// </summary>
public static class HoldemBot
{
    public static HoldemAct Decide(HoldemGame game, HoldemPlayer self, Random rng)
    {
        var wanted = game.Street == HoldemStreet.Preflop
            ? BeforeTheFlop(game, self, rng)
            : AfterTheFlop(game, self, rng);

        return Legalize(game, self, wanted);
    }

    // ------------------------------------------------------------------ preflop

    private static HoldemAct BeforeTheFlop(HoldemGame game, HoldemPlayer self, Random rng)
    {
        var strength = ChenScore(self.Hole[0], self.Hole[1]);
        var bigBlind = game.BigBlind;
        var toCall = game.ToCall(self);

        // Position: the fewer seats left to act behind us, the more we can play.
        var behind = game.SeatsYetToAct(self);
        strength += behind <= 1 ? 2 : behind >= 3 ? -1 : 0;

        // Short stacked there is no room to manoeuvre, so it is shove or give up.
        if (self.Chips <= bigBlind * 8)
        {
            if (strength >= 8) return Raise(game.MaxRaiseTo(self));
            return toCall == 0 ? Check() : Fold();
        }

        if (strength >= 12 || (strength >= 9 && rng.Next(3) > 0))
            return Raise(OpeningRaise(game, self, rng));

        if (strength >= 7)
            return toCall <= bigBlind * 3 ? Call() : Fold();

        if (strength >= 4)
            return toCall <= bigBlind ? Call() : Fold();

        // Nothing worth playing: take the free card, and now and then steal with air.
        if (toCall == 0)
            return rng.Next(9) == 0 ? Raise(OpeningRaise(game, self, rng)) : Check();

        return Fold();
    }

    /// <summary>
    /// The Chen formula: about -1 for the worst offsuit rags up to 20 for a pair of aces.
    /// Crude, but it ranks starting hands the way a decent player would.
    /// </summary>
    public static int ChenScore(Card a, Card b)
    {
        var high = (Rank)Math.Max((int)a.Rank, (int)b.Rank);
        var low = (Rank)Math.Min((int)a.Rank, (int)b.Rank);

        var score = high switch
        {
            Rank.Ace => 10.0,
            Rank.King => 8.0,
            Rank.Queen => 7.0,
            Rank.Jack => 6.0,
            _ => (int)high / 2.0,
        };

        if (a.Rank == b.Rank) score = Math.Max(score * 2, 5);
        if (a.Suit == b.Suit) score += 2;

        // Cards between the two are cards that cannot help a straight. A pair has no gap.
        var gap = Math.Max(0, (int)high - (int)low - 1);
        score -= gap switch { 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => 5 };

        // Small connected cards make straights, which the gap penalty alone undervalues.
        if (gap <= 1 && high < Rank.Queen && a.Rank != b.Rank) score += 1;

        return (int)Math.Ceiling(score);
    }

    /// <summary>Two and a half to three and a half big blinds, plus one for each limper already in.</summary>
    private static int OpeningRaise(HoldemGame game, HoldemPlayer self, Random rng)
    {
        var limpers = Math.Max(0, game.Pot / Math.Max(1, game.BigBlind) - 2);
        var target = game.BigBlind * (5 + rng.Next(3)) / 2 + limpers * game.BigBlind;
        return Math.Max(target, game.MinRaiseTo);
    }

    // ----------------------------------------------------------------- postflop

    private static HoldemAct AfterTheFlop(HoldemGame game, HoldemPlayer self, Random rng)
    {
        var made = PokerHand.Evaluate([.. self.Hole, .. game.Community]);
        var toCall = game.ToCall(self);
        var pot = game.Pot;

        var equity = RoughEquity(made, self, game);
        var drawing = HasRealDraw(self, game.Community);
        if (drawing) equity += 0.12;

        if (toCall == 0)
        {
            // Nothing to pay. Bet the good hands, semi-bluff the draws, trap once in a while.
            if (equity >= 0.80) return rng.Next(4) == 0 ? Check() : Raise(BetSize(game, self, pot, rng));
            if (equity >= 0.50) return rng.Next(5) == 0 ? Check() : Raise(BetSize(game, self, pot, rng));
            if (drawing && rng.Next(3) == 0) return Raise(BetSize(game, self, pot, rng));
            return rng.Next(10) == 0 ? Raise(BetSize(game, self, pot, rng)) : Check();
        }

        // The price the pot is laying: call this fraction of the time to break even.
        var price = toCall / (double)(pot + toCall);

        if (equity >= 0.85)
            return rng.Next(5) == 0 ? Call() : Raise(BetSize(game, self, pot + toCall, rng));
        if (equity > price + 0.06)
            return rng.Next(9) == 0 ? Raise(BetSize(game, self, pot + toCall, rng)) : Call();
        if (equity > price)
            return Call();

        return Fold();
    }

    /// <summary>What a made hand of this class is worth against one opponent, near enough to bet on.</summary>
    private static double RoughEquity(HandValue made, HoldemPlayer self, HoldemGame game)
    {
        var equity = made.Category switch
        {
            >= HandCategory.Straight => 0.92,
            HandCategory.ThreeOfAKind => 0.80,
            HandCategory.TwoPair => 0.66,
            HandCategory.Pair => 0.50,
            _ => 0.22,
        };

        // A hand made entirely of board cards is a hand everybody has.
        var fromHole = made.BestFive.Count(c => self.Hole.Contains(c));
        if (fromHole == 0) return Math.Min(equity, 0.28);

        // A pair that does not use the top board card is usually second best.
        if (made.Category == HandCategory.Pair && game.Community.Count > 0)
        {
            var topBoard = game.Community.Max(c => c.Rank);
            var paired = made.BestFive.GroupBy(c => c.Rank).First(g => g.Count() == 2).Key;
            if (paired < topBoard) equity -= 0.14;
        }

        return equity;
    }

    /// <summary>Four to a flush, or four to a straight, with cards still to come.</summary>
    private static bool HasRealDraw(HoldemPlayer self, IReadOnlyList<Card> board)
    {
        if (board.Count >= 5) return false; // the river is out; there is nothing to draw to

        var cards = self.Hole.Concat(board).ToList();
        if (cards.GroupBy(c => c.Suit).Max(g => g.Count()) == 4) return true;

        var ranks = cards.Select(c => (int)c.Rank).Distinct().OrderBy(r => r).ToList();
        if (ranks.Contains((int)Rank.Ace)) ranks.Insert(0, 1); // the ace plays low in the wheel

        var run = 1;
        for (var i = 1; i < ranks.Count; i++)
        {
            run = ranks[i] == ranks[i - 1] + 1 ? run + 1 : 1;
            if (run == 4) return true;
        }
        return false;
    }

    /// <summary>Somewhere between half the pot and the whole pot, never below the minimum.</summary>
    private static int BetSize(HoldemGame game, HoldemPlayer self, int pot, Random rng)
    {
        var target = self.StreetBet + game.ToCall(self) + pot * (5 + rng.Next(4)) / 10;
        return Math.Max(target, game.MinRaiseTo);
    }

    // ------------------------------------------------------------------- safety

    /// <summary>
    /// Turns whatever the bot wanted into something the engine will accept. This is the one
    /// guarantee that matters: a bot that stalls or throws takes the whole table down with it.
    /// </summary>
    private static HoldemAct Legalize(HoldemGame game, HoldemPlayer self, HoldemAct act)
    {
        var toCall = game.ToCall(self);

        switch (act.Move)
        {
            case HoldemMove.Check when toCall > 0:
                return Call();

            case HoldemMove.Call when toCall == 0:
                return Check();

            case HoldemMove.Fold when toCall == 0:
                return Check(); // never give up a hand that costs nothing to keep

            case HoldemMove.Raise:
            {
                if (!game.CanRaise(self)) return toCall == 0 ? Check() : Call();
                var min = game.MinRaiseTo;
                var max = game.MaxRaiseTo(self);
                // Short of a full raise, moving in for everything is the only raise left.
                return Raise(max <= min ? max : Math.Clamp(act.Amount, min, max));
            }

            default:
                return act;
        }
    }

    private static HoldemAct Fold() => new(HoldemMove.Fold);
    private static HoldemAct Check() => new(HoldemMove.Check);
    private static HoldemAct Call() => new(HoldemMove.Call);
    private static HoldemAct Raise(int to) => new(HoldemMove.Raise, to);
}

/// <summary>Adapts the <see cref="HoldemGame"/> engine to the tavern platform.</summary>
public sealed class HoldemModule : IGameModule
{
    public const string Type = "holdem";
    private const string ChipsKey = "startingChips";
    private const string SmallBlindKey = "smallBlind";
    private const string BlindsUpKey = "blindsUpEvery";

    public static GameDescriptor Descriptor { get; } = new(
        Type: Type,
        DisplayName: "Texas Hold'em",
        Blurb: "No-limit hold'em, played down to one winner. Two cards each and five on the board.",
        MinPlayers: HoldemGame.MinPlayers,
        MaxPlayers: HoldemGame.MaxPlayers,
        Options:
        [
            new GameOption(ChipsKey, "Starting chips", 500, 10000, 1000),
            new GameOption(SmallBlindKey, "Small blind", 5, 100, 10),
            new GameOption(BlindsUpKey, "Blinds up every (0 = never)", 0, 20, 8),
        ],
        Messages:
        [
            (typeof(HoldemAct), "holdem.act"),
            (typeof(HoldemHandStarted), "holdem.handStarted"),
            (typeof(HoldemYourCards), "holdem.yourCards"),
            (typeof(HoldemBlindPosted), "holdem.blindPosted"),
            (typeof(HoldemActed), "holdem.acted"),
            (typeof(HoldemStreetDealt), "holdem.streetDealt"),
            (typeof(HoldemShowdown), "holdem.showdown"),
            (typeof(HoldemHandEnded), "holdem.handEnded"),
            (typeof(HoldemSnapshot), "holdem.snapshot"),
        ],
        Create: options => new HoldemModule(
            Deck.Shuffled, options[ChipsKey], options[SmallBlindKey], options[BlindsUpKey]));

    private readonly HoldemGame _game;
    private readonly HashSet<string> _bots = new();

    public HoldemModule(Func<Deck> newDeck, int startingChips, int smallBlind, int blindsUpEvery) =>
        _game = new HoldemGame(newDeck, startingChips, smallBlind, blindsUpEvery);

    public string GameType => Type;
    public int MinPlayers => HoldemGame.MinPlayers;
    public int MaxPlayers => HoldemGame.MaxPlayers;
    public GamePhase Phase => _game.Phase;

    public string? CurrentActorId =>
        _game.Phase == GamePhase.Playing ? _game.CurrentPlayer?.Id : null;

    public PlayerPublic[] Roster() =>
        _game.Players
            .Select(p => new PlayerPublic(p.Id, p.Name, p.Chips, p.Eliminated, _bots.Contains(p.Id)))
            .ToArray();

    public void AddPlayer(string id, string name, bool isBot)
    {
        _game.AddPlayer(id, name);
        if (isBot) _bots.Add(id);
    }

    public IReadOnlyList<Emit> RemovePlayer(string id)
    {
        var wasPlaying = _game.Phase == GamePhase.Playing;
        var events = _game.RemovePlayer(id);
        _bots.Remove(id);

        if (!wasPlaying) return [];

        var emits = ToEmits(events);
        if (events.Count == 0 && _game.Phase == GamePhase.Playing)
            emits.Add(new ToAll(new HoldemSnapshot(_game.Snapshot()))); // redraw without the empty chair
        return emits;
    }

    public IReadOnlyList<Emit> Start() => ToEmits(_game.StartGame());

    public IReadOnlyList<Emit> Handle(string playerId, NetMessage move) => ToEmits(move switch
    {
        HoldemAct act => _game.Act(playerId, act.Move, act.Amount),
        _ => throw new InvalidOperationException($"'{move.GetType().Name}' is not a hold'em move."),
    });

    public NetMessage? DecideBotMove(string botId, Random rng)
    {
        if (_game.Phase != GamePhase.Playing) return null;
        var self = _game.CurrentPlayer;
        return self?.Id == botId ? HoldemBot.Decide(_game, self, rng) : null;
    }

    public IReadOnlyList<NetMessage> CatchUp() =>
        _game.Phase == GamePhase.Playing ? [new HoldemSnapshot(_game.Snapshot())] : [];

    /// <summary>Turns the engine's chain of events into messages, with pauses where a table would breathe.</summary>
    private List<Emit> ToEmits(IReadOnlyList<HoldemEvent> events)
    {
        var emits = new List<Emit>();
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case HoldemEventKind.HandStarted:
                    emits.Add(new ToAll(new HoldemHandStarted(e.Table)));
                    // The only place hole cards go out, and only ever to the seat holding them.
                    foreach (var deal in e.Deals ?? [])
                        emits.Add(new ToPlayer(deal.PlayerId, new HoldemYourCards(deal.Cards)));
                    break;

                case HoldemEventKind.BlindPosted:
                    emits.Add(new ToAll(new HoldemBlindPosted(e.PlayerId!, e.Amount, e.Big, e.Table)));
                    break;

                case HoldemEventKind.Acted:
                    emits.Add(new ToAll(new HoldemActed(e.PlayerId!, e.Move, e.Amount, e.Table)));
                    break;

                case HoldemEventKind.StreetDealt:
                    emits.Add(new Pause(PauseKind.Beat)); // let the last action land before the board changes
                    emits.Add(new ToAll(new HoldemStreetDealt(e.Street, e.Cards ?? [], e.Table)));
                    break;

                case HoldemEventKind.Showdown:
                    emits.Add(new Pause(PauseKind.Beat));
                    emits.Add(new ToAll(new HoldemShowdown(e.Reveals ?? [], e.Table)));
                    break;

                case HoldemEventKind.HandEnded:
                    emits.Add(new ToAll(new HoldemHandEnded(e.Awards ?? [], e.BustedOut ?? [], e.Table)));
                    emits.Add(new Pause(PauseKind.RoundBreak)); // time to read who took what
                    break;

                case HoldemEventKind.GameOver:
                    emits.Add(new ToAll(new GameEnded(e.WinnerId ?? "", Roster())));
                    break;
            }
        }
        return emits;
    }
}
