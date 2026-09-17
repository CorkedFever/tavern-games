using TavernGames.Core.Cards;

namespace TavernGames.Core.Games.Holdem;

public sealed class HoldemPlayer
{
    public required string Id { get; init; }
    public required string Name { get; set; }

    /// <summary>Chips behind, i.e. not yet pushed into the pot.</summary>
    public int Chips { get; internal set; }

    /// <summary>Chips pushed in over the whole hand, every street.</summary>
    public int Committed { get; internal set; }

    /// <summary>Chips pushed in on the current street only, which is what a call has to match.</summary>
    public int StreetBet { get; internal set; }

    public List<Card> Hole { get; } = new();

    public bool Folded { get; internal set; }

    /// <summary>Busted: no chips left after a hand settled. Stays at the table as a spectator.</summary>
    public bool Eliminated { get; internal set; }

    /// <summary>Has shown their hole cards at a showdown. Folded hands are never revealed.</summary>
    public bool Revealed { get; internal set; }

    /// <summary>Has acted since the last full bet or raise on this street.</summary>
    internal bool Acted { get; set; }

    /// <summary>
    /// Cleared when an all-in for less than a full raise passes a seat that already acted:
    /// the betting is not reopened for them, so they may only call or fold.
    /// </summary>
    internal bool MayRaise { get; set; } = true;

    public bool HasCards => Hole.Count > 0;

    /// <summary>Still holding cards and still able to win the hand.</summary>
    public bool Live => HasCards && !Folded;

    public bool AllIn => Chips == 0 && Live;
}

public enum HoldemEventKind
{
    HandStarted,
    BlindPosted,
    Acted,
    StreetDealt,
    Showdown,
    HandEnded,
    GameOver,
}

/// <summary>One seat's private hole cards, carried on <see cref="HoldemEventKind.HandStarted"/>.</summary>
public sealed record HoldemDeal(string PlayerId, string[] Cards);

/// <summary>
/// Something that happened at the table, with the table as the public saw it at that
/// moment. One action can cause a whole chain (a call closes the betting, the river falls,
/// hands are shown, pots are pushed, the next hand starts), so every engine call returns
/// the chain in order.
/// </summary>
public sealed record HoldemEvent(
    HoldemEventKind Kind,
    HoldemTable Table,
    string? PlayerId = null,
    int Amount = 0,
    HoldemMove Move = HoldemMove.Fold,
    bool Big = false,
    HoldemStreet Street = HoldemStreet.Preflop,
    string[]? Cards = null,
    HoldemDeal[]? Deals = null,
    HoldemReveal[]? Reveals = null,
    HoldemAward[]? Awards = null,
    string[]? BustedOut = null,
    string? WinnerId = null);

/// <summary>
/// No-limit Texas hold'em played as a sit-and-go: everyone starts with the same stack, a
/// seat with nothing left after a hand is eliminated, and the last player holding chips
/// wins. Two hole cards each, a five card board dealt as flop, turn and river, with a
/// betting round before each.
///
/// Where the rules leave room, this engine plays the most widely used convention:
///
/// <list type="bullet">
/// <item>The button moves one live seat clockwise per hand. Blinds move with it and simply
/// skip eliminated seats: no dead button and no dead blind.</item>
/// <item>Heads-up, the button posts the small blind, acts first before the flop and last on
/// every later street.</item>
/// <item>A seat that cannot cover its blind posts what it has and is all-in. The full big
/// blind is still the bet the other seats have to call.</item>
/// <item>The minimum raise is the size of the last full bet or raise on the street, and
/// never less than the big blind. An all-in for less than that is allowed but does not
/// reopen the betting for anyone who has already acted since the last full raise; they may
/// only call or fold.</item>
/// <item>The big blind gets the option to check or raise when nobody raised before the flop.</item>
/// <item>Chips no opponent could match are returned to the seat that bet them before any pot
/// is built, so an uncalled bet is never reported as a win.</item>
/// <item>Split pots that do not divide evenly give the odd chips to the tied seats nearest
/// the button's left.</item>
/// <item>No antes, no rake, and no burn cards: a burn card is never shown, so burning would
/// only make stacked-deck tests harder to read.</item>
/// <item>A player who leaves mid-hand folds at once. What they pushed in stays in the pot as
/// far as an opponent had matched it; the rest of their stack, a bet nobody could call
/// included, leaves the game with them.</item>
/// </list>
/// </summary>
public sealed class HoldemGame
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 6;

    /// <summary>
    /// How many hands in a row may need no decision at all before the table is called on
    /// chips. Any hand where a seat still has something to bet ends the run, so this only
    /// exists so a pathological deal that keeps splitting every pot cannot hang the room.
    /// </summary>
    private const int StalemateHands = 500;

    private readonly List<HoldemPlayer> _players = new();
    private readonly List<Card> _community = new();

    /// <summary>Chips left in the pot by players who walked out mid-hand. Nobody can win them back.</summary>
    private readonly List<Stake> _deadStakes = new();

    private readonly Func<Deck> _newDeck;
    private Deck _deck;

    private int _button = -1; // index into _players
    private int _actor = -1;  // index into _players, -1 when nobody is on the clock
    private int _currentBet;
    private int _lastRaiseSize;
    private int _chipCap;

    public HoldemGame(Func<Deck> newDeck, int startingChips = 1000, int smallBlind = 10, int blindsUpEvery = 8)
    {
        if (smallBlind < 1) throw new ArgumentOutOfRangeException(nameof(smallBlind));
        if (startingChips < smallBlind * 2) throw new ArgumentOutOfRangeException(nameof(startingChips));
        if (blindsUpEvery < 0) throw new ArgumentOutOfRangeException(nameof(blindsUpEvery));

        _newDeck = newDeck;
        _deck = newDeck();
        StartingChips = startingChips;
        BaseSmallBlind = smallBlind;
        BlindsUpEvery = blindsUpEvery;
    }

    public int StartingChips { get; }

    /// <summary>The small blind at level one. The live blind is <see cref="SmallBlind"/>.</summary>
    public int BaseSmallBlind { get; }

    /// <summary>Hands between blind rises, or 0 when the blinds never move.</summary>
    public int BlindsUpEvery { get; }

    public int Hand { get; private set; }
    public HoldemStreet Street { get; private set; }
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public string? WinnerId { get; private set; }

    public IReadOnlyList<HoldemPlayer> Players => _players;
    public IReadOnlyList<Card> Community => _community;

    /// <summary>The seat that must act, or null between hands and once the betting is finished.</summary>
    public HoldemPlayer? CurrentPlayer => _actor >= 0 && _actor < _players.Count ? _players[_actor] : null;

    public string? ButtonId => _button >= 0 && _button < _players.Count ? _players[_button].Id : null;

    /// <summary>1 while the blinds are at their opening level, rising by one every <see cref="BlindsUpEvery"/> hands.</summary>
    public int BlindLevel => BlindsUpEvery <= 0 || Hand < 1 ? 1 : (Hand - 1) / BlindsUpEvery + 1;

    /// <summary>Hands left before the blinds double, or 0 when they never do.</summary>
    public int HandsUntilBlindsUp => BlindsUpEvery <= 0 || Hand < 1 ? 0 : BlindsUpEvery - (Hand - 1) % BlindsUpEvery;

    /// <summary>
    /// The live small blind. It doubles each level but stops once it covers every chip on
    /// the table, which both keeps a long game from overflowing and changes nothing: past
    /// that point the blinds already put everyone all-in.
    /// </summary>
    public int SmallBlind
    {
        get
        {
            var cap = Math.Max(BaseSmallBlind, _chipCap / 2);
            var blind = BaseSmallBlind;
            for (var level = 1; level < BlindLevel && blind < cap; level++) blind *= 2;
            return Math.Min(blind, cap);
        }
    }

    public int BigBlind => SmallBlind * 2;

    /// <summary>Everything pushed into the pot this hand, including chips left behind by departed seats.</summary>
    public int Pot => Stakes().Sum(s => s.Contributed);

    /// <summary>The highest street bet anyone has to match.</summary>
    public int CurrentBet => _currentBet;

    // ------------------------------------------------------------------- seating

    public HoldemPlayer AddPlayer(string id, string name)
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("Players can only join while in the lobby.");
        if (_players.Count >= MaxPlayers)
            throw new InvalidOperationException($"A hold'em table seats at most {MaxPlayers}.");
        if (_players.Any(p => p.Id == id))
            throw new InvalidOperationException($"Player '{id}' is already at the table.");

        var player = new HoldemPlayer { Id = id, Name = name };
        _players.Add(player);
        return player;
    }

    /// <summary>
    /// A departing seat folds immediately. Whatever of their money an opponent had already
    /// matched stays in the pot as dead money; the rest of their stack, uncalled chips
    /// included, leaves the game with them, and play moves on without them.
    /// </summary>
    public IReadOnlyList<HoldemEvent> RemovePlayer(string id)
    {
        var index = _players.FindIndex(p => p.Id == id);
        if (index < 0) return [];

        var player = _players[index];
        var wasPlaying = Phase == GamePhase.Playing;
        var wasActor = _actor == index;

        // A bet nobody could match is always handed back to whoever made it, and walking out
        // is no way round that: leaving the whole of an uncalled shove behind would make
        // closing the window a way of handing a stack to whoever wins the hand.
        var dead = Math.Min(player.Committed, Matched(player));
        if (wasPlaying && dead > 0)
            _deadStakes.Add(new Stake(player.Id, dead, Folded: true));

        _players.RemoveAt(index);
        // Both markers are positions, so shift them to keep pointing at the same seats. When
        // the actor themselves left, landing one seat earlier is exactly what we want: the
        // next scan then starts from the seat after the empty chair.
        if (index <= _button) _button--;
        if (_actor >= 0 && index <= _actor) _actor--;
        // Off the front of the list the button falls off the table entirely. Wrapping from -1
        // and from the last seat walks the same order, so nothing about play changes, but the
        // marker has to name a seat or the room loses its dealer button for the rest of the hand.
        if (wasPlaying && _button < 0 && _players.Count > 0) _button = _players.Count - 1;

        if (!wasPlaying) return [];

        var events = new List<HoldemEvent>();
        if (_players.Count == 0)
        {
            Phase = GamePhase.GameOver;
            return events;
        }

        if (LiveCount <= 1)
        {
            SettleHand(events); // their fold ended the hand
        }
        else if (wasActor)
        {
            _actor = NextActorFrom(_actor);
            if (_actor < 0) CloseBettingRound(events);
        }

        PlayOn(events);
        return events;
    }

    public IReadOnlyList<HoldemEvent> StartGame()
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("The game has already started.");
        if (_players.Count < MinPlayers)
            throw new InvalidOperationException($"Hold'em needs at least {MinPlayers} players.");

        foreach (var p in _players) p.Chips = StartingChips;
        _chipCap = StartingChips * _players.Count;
        Phase = GamePhase.Playing;

        var events = new List<HoldemEvent>();
        PlayOn(events);
        return events;
    }

    // -------------------------------------------------------------------- acting

    /// <summary>
    /// Applies one action from the seat on the clock. <paramref name="amount"/> is the
    /// raise-TO total for the street and is ignored by every other move.
    /// </summary>
    public IReadOnlyList<HoldemEvent> Act(string playerId, HoldemMove move, int amount = 0)
    {
        if (Phase != GamePhase.Playing)
            throw new InvalidOperationException("There is no hand in progress.");

        var player = Find(playerId);
        if (CurrentPlayer?.Id != playerId)
            throw new InvalidOperationException("It is not your turn.");

        var seat = _actor;
        var toCall = ToCall(player);
        var reported = 0;

        switch (move)
        {
            case HoldemMove.Fold:
                player.Folded = true;
                player.Acted = true;
                break;

            case HoldemMove.Check:
                if (toCall > 0)
                    throw new InvalidOperationException($"You have {toCall} to call, so you can't check.");
                player.Acted = true;
                break;

            case HoldemMove.Call:
                if (toCall == 0)
                    throw new InvalidOperationException("There is nothing to call; check instead.");
                reported = toCall;
                Commit(player, toCall);
                player.Acted = true;
                break;

            case HoldemMove.Raise:
                reported = RaiseTo(player, amount);
                break;

            default:
                throw new ArgumentException($"'{move}' is not a hold'em move.", nameof(move));
        }

        // Move the clock on before the event is built: its snapshot has to name the seat that
        // has to act next, not the one that just did, or every client draws the wrong turn.
        // A fold can leave one seat standing, which ends the hand there and then.
        var handIsOver = LiveCount <= 1;
        _actor = handIsOver ? -1 : NextActorFrom(seat);

        var events = new List<HoldemEvent> { Event(HoldemEventKind.Acted, playerId, reported, move) };

        if (handIsOver) SettleHand(events);
        else if (_actor < 0) CloseBettingRound(events);

        PlayOn(events);
        return events;
    }

    /// <summary>What <paramref name="player"/> must put in to match the current bet, capped at their stack.</summary>
    public int ToCall(HoldemPlayer player) => Math.Clamp(_currentBet - player.StreetBet, 0, player.Chips);

    /// <summary>
    /// The smallest legal raise-to on this street: the current bet plus the last full bet or
    /// raise. A seat whose whole stack falls short of it may still move all-in for less.
    /// </summary>
    public int MinRaiseTo => _currentBet + _lastRaiseSize;

    /// <summary>Everything <paramref name="player"/> has, counting what is already in front of them.</summary>
    public int MaxRaiseTo(HoldemPlayer player) => player.StreetBet + player.Chips;

    /// <summary>
    /// False when the seat may only call or fold: it is short, a short all-in closed the
    /// action on it, or every opponent left in the hand is already all-in. That last case is
    /// the one people forget: with nobody able to put in another chip there is nothing to
    /// raise, so no-limit simply does not offer the move.
    /// </summary>
    public bool CanRaise(HoldemPlayer player) =>
        player.MayRaise
        && MaxRaiseTo(player) > _currentBet
        && _players.Any(other => other != player && other.Live && !other.AllIn);

    /// <summary>How many seats still act behind this one on this street. Position, as one number.</summary>
    public int SeatsYetToAct(HoldemPlayer player)
    {
        var index = _players.IndexOf(player);
        if (index < 0) return 0;
        return SeatsAfter(index).Take(_players.Count - 1).Count(i => MustAct(_players[i]));
    }

    /// <summary>The table as the public may see it right now.</summary>
    public HoldemTable Snapshot()
    {
        var actor = CurrentPlayer;
        var stakes = Stakes();

        return new HoldemTable(
            Hand: Hand,
            SmallBlind: SmallBlind,
            BigBlind: BigBlind,
            BlindLevel: BlindLevel,
            HandsUntilBlindsUp: HandsUntilBlindsUp,
            Street: Street,
            Community: _community.Select(c => c.Code).ToArray(),
            Pot: stakes.Sum(s => s.Contributed),
            Pots: PotMath.BuildPots(stakes)
                .Select(p => new HoldemPotView(p.Amount, p.Eligible.ToArray())).ToArray(),
            ButtonId: ButtonId,
            CurrentPlayerId: actor?.Id,
            CurrentBet: _currentBet,
            ToCall: actor is null ? 0 : ToCall(actor),
            // Clamped so a client can always build a slider from min to max, even for a seat
            // whose only legal raise is an all-in below the nominal minimum.
            MinRaiseTo: actor is null ? 0 : Math.Min(MinRaiseTo, MaxRaiseTo(actor)),
            MaxRaiseTo: actor is null ? 0 : MaxRaiseTo(actor),
            CanRaise: actor is not null && CanRaise(actor),
            Seats: _players.Select(SeatOf).ToArray());
    }

    // --------------------------------------------------------------- hand flow

    /// <summary>
    /// Runs the table forward until it needs a decision again, dealing the next hand whenever
    /// the last one is finished. A hand can settle without anyone having acted (the blinds
    /// put every seat all-in), and chaining those as a loop rather than as recursion keeps
    /// the stack flat however long the run turns out to be. <see cref="StalemateHands"/> is
    /// the backstop: a real deal always hands somebody chips to act with long before then.
    /// </summary>
    private void PlayOn(List<HoldemEvent> events)
    {
        var handsWithoutADecision = 0;
        while (Phase == GamePhase.Playing && _actor < 0)
        {
            if (++handsWithoutADecision > StalemateHands)
            {
                EndGame(events);
                return;
            }
            StartHand(events);
        }
    }

    private void StartHand(List<HoldemEvent> events)
    {
        if (_players.Count(p => !p.Eliminated) <= 1)
        {
            EndGame(events);
            return;
        }

        Hand++;
        Street = HoldemStreet.Preflop;
        _community.Clear();
        _deadStakes.Clear();
        _deck = _newDeck();

        foreach (var p in _players)
        {
            p.Hole.Clear();
            p.Committed = 0;
            p.StreetBet = 0;
            p.Folded = false;
            p.Revealed = false;
            p.Acted = false;
            p.MayRaise = true;
        }

        _button = SeatsAfter(_button).First(i => !_players[i].Eliminated);

        // Deal two passes round the table starting to the button's left, the way a dealer does.
        var order = SeatsAfter(_button).Where(i => !_players[i].Eliminated).ToList();
        for (var pass = 0; pass < 2; pass++)
            foreach (var i in order)
                _players[i].Hole.Add(_deck.Draw());

        // Heads-up the button posts the small blind; otherwise it is the seat to its left.
        var smallSeat = order.Count == 2 ? _button : order[0];
        var bigSeat = order.Count == 2 ? order[0] : order[1];

        // The bet to match is the full big blind even when the seat posting it is short.
        _currentBet = BigBlind;
        _lastRaiseSize = BigBlind;
        var smallPosted = PostBlind(smallSeat, SmallBlind);
        var bigPosted = PostBlind(bigSeat, BigBlind);

        // From the big blind's left: the seat after the big blind in a full ring, and the
        // button heads-up, which is the same seat once the wrap is taken into account.
        _actor = NextActorFrom(bigSeat);

        events.Add(Event(HoldemEventKind.HandStarted) with
        {
            Deals = order.Select(i => new HoldemDeal(
                _players[i].Id, _players[i].Hole.Select(c => c.Code).ToArray())).ToArray(),
        });
        events.Add(Event(HoldemEventKind.BlindPosted, _players[smallSeat].Id, smallPosted));
        events.Add(Event(HoldemEventKind.BlindPosted, _players[bigSeat].Id, bigPosted) with { Big = true });

        // The blinds can put everyone all-in before a single voluntary bet.
        if (_actor < 0) CloseBettingRound(events);
    }

    private int PostBlind(int seat, int blind)
    {
        var player = _players[seat];
        var posted = Math.Min(blind, player.Chips);
        Commit(player, posted);
        return posted;
    }

    /// <summary>Called when nobody is left to act: closes the street, or the hand.</summary>
    private void CloseBettingRound(List<HoldemEvent> events)
    {
        if (LiveCount <= 1)
        {
            SettleHand(events);
            return;
        }

        // Once at most one live seat still has chips behind, no further betting is possible.
        if (_players.Count(p => p.Live && !p.AllIn) <= 1)
        {
            RunOut(events);
            return;
        }

        if (Street == HoldemStreet.River)
        {
            GoToShowdown(events);
            return;
        }

        ResetStreet();
        Street = Next(Street);
        var dealt = DealBoard(Street);
        _actor = NextActorFrom(_button); // first live seat to the button's left, heads-up included
        events.Add(Event(HoldemEventKind.StreetDealt) with { Street = Street, Cards = dealt });

        if (_actor < 0) CloseBettingRound(events);
    }

    /// <summary>Everyone who can act is all-in: turn the live hands up and run the board out.</summary>
    private void RunOut(List<HoldemEvent> events)
    {
        ResetStreet();
        _actor = -1;

        if (Street != HoldemStreet.River)
        {
            Reveal(events, describe: false);
            while (Street != HoldemStreet.River)
            {
                Street = Next(Street);
                var dealt = DealBoard(Street);
                events.Add(Event(HoldemEventKind.StreetDealt) with { Street = Street, Cards = dealt });
            }
        }

        GoToShowdown(events);
    }

    private void GoToShowdown(List<HoldemEvent> events)
    {
        ResetStreet();
        _actor = -1;
        Street = HoldemStreet.Showdown;
        Reveal(events, describe: true);
        SettleHand(events);
    }

    /// <summary>Turns the live hands face up. A hand that folded is never shown, before or after.</summary>
    private void Reveal(List<HoldemEvent> events, bool describe)
    {
        var live = _players.Where(p => p.Live).ToList();
        foreach (var p in live) p.Revealed = true;

        var reveals = live.Select(p => new HoldemReveal(
            p.Id,
            p.Hole.Select(c => c.Code).ToArray(),
            describe ? PokerHand.Describe(Evaluate(p)) : null)).ToArray();

        events.Add(Event(HoldemEventKind.Showdown) with { Reveals = reveals });
    }

    private void SettleHand(List<HoldemEvent> events)
    {
        _actor = -1;
        var awards = new List<HoldemAward>();

        // Chips no opponent could match go back before any pot is built, so they are never
        // dressed up as a win. What is left is exactly the money that was actually contested.
        if (RefundUncalled() is { } refund)
            awards.Add(new HoldemAward(refund.PlayerId, refund.Amount, HoldemAwardKind.Uncalled, null));

        var pots = PotMath.BuildPots(Stakes());
        var hands = ShowdownHands();
        var seatOrder = SeatsAfter(_button).Select(i => _players[i].Id).ToList();

        for (var i = 0; i < pots.Count; i++)
        {
            // Awarded one pot at a time so the table can be told who took which.
            foreach (var (id, amount) in PotMath.Award([pots[i]], hands, seatOrder))
            {
                var winner = Find(id);
                winner.Chips += amount;
                awards.Add(new HoldemAward(
                    id, amount,
                    i == 0 ? HoldemAwardKind.Main : HoldemAwardKind.Side,
                    winner.Revealed ? PokerHand.Describe(hands[id]) : null));
            }
        }

        // The pot is empty now, but the cards stay on the table so the reveal is still on
        // screen through the break before the next hand clears them.
        foreach (var p in _players)
        {
            p.Committed = 0;
            p.StreetBet = 0;
        }
        _deadStakes.Clear();
        _currentBet = 0;

        var busted = _players.Where(p => !p.Eliminated && p.Chips == 0).ToList();
        foreach (var p in busted) p.Eliminated = true;

        events.Add(Event(HoldemEventKind.HandEnded) with
        {
            Awards = awards.ToArray(),
            BustedOut = busted.Select(p => p.Id).ToArray(),
        });

        if (_players.Count(p => !p.Eliminated) <= 1) EndGame(events);
    }

    private void EndGame(List<HoldemEvent> events)
    {
        Phase = GamePhase.GameOver;
        _actor = -1;
        // Normally exactly one seat is left; the chip count only matters if a stalemate was called.
        WinnerId = (_players.Where(p => !p.Eliminated).MaxBy(p => p.Chips) ?? _players.MaxBy(p => p.Chips))?.Id;
        events.Add(Event(HoldemEventKind.GameOver) with { WinnerId = WinnerId });
    }

    // ------------------------------------------------------------------ betting

    private int RaiseTo(HoldemPlayer player, int to)
    {
        if (!CanRaise(player))
            throw new InvalidOperationException("You can only call or fold.");

        var max = MaxRaiseTo(player);
        if (to > max)
            throw new InvalidOperationException($"You can only put in {max}.");
        if (to <= _currentBet)
            throw new InvalidOperationException($"A raise has to beat {_currentBet}.");

        if (to < MinRaiseTo && to != max)
            throw new InvalidOperationException($"The minimum raise is to {MinRaiseTo}.");

        var raiseSize = to - _currentBet;
        var isFullRaise = raiseSize >= _lastRaiseSize;

        Commit(player, to - player.StreetBet);
        _currentBet = to;
        player.Acted = true;

        if (isFullRaise)
        {
            _lastRaiseSize = raiseSize;
            // A full raise puts everyone else back on the clock, free to raise again.
            foreach (var other in _players.Where(p => p != player && p.Live && !p.AllIn))
            {
                other.Acted = false;
                other.MayRaise = true;
            }
        }
        else
        {
            // An all-in short of a full raise still has to be called, but it does not reopen
            // the betting: anyone who already acted since the last full raise may only call.
            foreach (var other in _players.Where(p => p != player && p.Live && !p.AllIn && p.Acted))
                other.MayRaise = false;
        }

        return to;
    }

    private static void Commit(HoldemPlayer player, int chips)
    {
        chips = Math.Clamp(chips, 0, player.Chips);
        player.Chips -= chips;
        player.StreetBet += chips;
        player.Committed += chips;
    }

    private void ResetStreet()
    {
        _currentBet = 0;
        _lastRaiseSize = BigBlind; // the first bet on a new street is at least the big blind
        foreach (var p in _players)
        {
            p.StreetBet = 0;
            p.Acted = false;
            p.MayRaise = true;
        }
    }

    /// <summary>A seat owes an action while it can still act and either has not acted or is short of the bet.</summary>
    private bool MustAct(HoldemPlayer p) =>
        p.Live && !p.AllIn && (!p.Acted || p.StreetBet < _currentBet);

    private int NextActorFrom(int index) =>
        SeatsAfter(index).FirstOrDefault(i => MustAct(_players[i]), -1);

    /// <summary>Every seat index in order starting after <paramref name="index"/>, wrapping once.</summary>
    private IEnumerable<int> SeatsAfter(int index)
    {
        var count = _players.Count;
        for (var step = 1; step <= count; step++)
            yield return ((index + step) % count + count) % count;
    }

    private int LiveCount => _players.Count(p => p.Live);

    // ------------------------------------------------------------------- payout

    /// <summary>
    /// Hands the top contributor back whatever nobody matched. Returns null when every chip
    /// was covered, or when the overage belongs to a seat that has already left the game.
    /// </summary>
    private (string PlayerId, int Amount)? RefundUncalled()
    {
        var contributions = _players
            .Where(p => p.Committed > 0)
            .Select(p => (Id: p.Id, Amount: p.Committed))
            .Concat(_deadStakes.Select(s => (Id: s.PlayerId, Amount: s.Contributed)))
            .OrderByDescending(c => c.Amount)
            .ToList();

        if (contributions.Count == 0) return null;

        var top = contributions[0];
        var matched = contributions.Count > 1 ? contributions[1].Amount : 0;
        if (top.Amount <= matched) return null;

        var player = _players.FirstOrDefault(p => p.Id == top.Id);
        if (player is null) return null; // a departed seat's chips stay on the table

        var refund = top.Amount - matched;
        player.Chips += refund;
        player.Committed -= refund;
        return (player.Id, refund);
    }

    /// <summary>
    /// The most anybody else has in the pot, which is the ceiling on what one seat's own
    /// stake could ever be called for. Departed seats count: their chips are in there too.
    /// </summary>
    private int Matched(HoldemPlayer player) =>
        _players
            .Where(p => p != player)
            .Select(p => p.Committed)
            .Concat(_deadStakes.Select(s => s.Contributed))
            .DefaultIfEmpty(0)
            .Max();

    private List<Stake> Stakes() =>
        _players
            .Where(p => p.Committed > 0)
            .Select(p => new Stake(p.Id, p.Committed, !p.Live))
            .Concat(_deadStakes)
            .ToList();

    /// <summary>
    /// The hands the pots are settled against. When everyone else folded the last seat
    /// standing is the only contender, so its value never has to be compared with anything.
    /// </summary>
    private Dictionary<string, HandValue> ShowdownHands()
    {
        var live = _players.Where(p => p.Live).ToList();
        return live.Count <= 1
            ? live.ToDictionary(p => p.Id, _ => default(HandValue))
            : live.ToDictionary(p => p.Id, Evaluate);
    }

    private HandValue Evaluate(HoldemPlayer player) =>
        PokerHand.Evaluate([.. player.Hole, .. _community]);

    // ------------------------------------------------------------------ helpers

    private string[] DealBoard(HoldemStreet street)
    {
        var count = street == HoldemStreet.Flop ? 3 : 1;
        var dealt = new List<Card>(count);
        for (var i = 0; i < count; i++)
        {
            var card = _deck.Draw();
            _community.Add(card);
            dealt.Add(card);
        }
        return dealt.Select(c => c.Code).ToArray();
    }

    private static HoldemStreet Next(HoldemStreet street) => street switch
    {
        HoldemStreet.Preflop => HoldemStreet.Flop,
        HoldemStreet.Flop => HoldemStreet.Turn,
        _ => HoldemStreet.River,
    };

    private HoldemSeat SeatOf(HoldemPlayer p) => new(
        p.Id,
        p.Chips,
        p.StreetBet,
        p.Committed,
        CardsFor(p),
        p.Folded,
        p.AllIn && !p.Eliminated,
        p.Eliminated);

    /// <summary>The only place hole cards reach the public, and only ever as backs or a shown hand.</summary>
    private static string[] CardsFor(HoldemPlayer p)
    {
        if (!p.HasCards || p.Folded) return [];
        return p.Revealed
            ? p.Hole.Select(c => c.Code).ToArray()
            : p.Hole.Select(_ => Card.HiddenCode).ToArray();
    }

    private HoldemEvent Event(
        HoldemEventKind kind, string? playerId = null, int amount = 0, HoldemMove move = HoldemMove.Fold) =>
        new(kind, Snapshot(), playerId, amount, move);

    private HoldemPlayer Find(string playerId) =>
        _players.FirstOrDefault(p => p.Id == playerId)
        ?? throw new InvalidOperationException("You're not seated at this table.");
}
