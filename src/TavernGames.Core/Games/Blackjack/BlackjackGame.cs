using TavernGames.Core.Cards;

namespace TavernGames.Core.Games.Blackjack;

public sealed class BjPlayer
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public int Chips { get; internal set; }
    public int Bet { get; internal set; }
    public List<Card> Cards { get; } = new();
    public BjHandState State { get; internal set; } = BjHandState.Waiting;

    /// <summary>Can't cover the minimum bet any more: sits out the rest of the game.</summary>
    public bool Out { get; internal set; }
}

public enum BjEventKind
{
    BettingOpened,
    BetPlaced,
    Dealt,
    Played,
    DealerPlayed,
    RoundSettled,
    GameOver,
}

/// <summary>
/// Something that happened at the table, with the table as it looked at that moment.
/// One move can cause a chain (a bust ends the round, the dealer plays, bets settle,
/// the next round opens), so every engine call returns the whole chain in order.
/// </summary>
public sealed record BjEvent(
    BjEventKind Kind,
    BjTable Table,
    string? PlayerId = null,
    int Amount = 0,
    string? Action = null,
    string? Card = null,
    bool Reveal = false,
    BjPayout[]? Payouts = null,
    string? WinnerId = null);

public static class BjMath
{
    /// <summary>Best total not over 21 if one exists. "Soft" means an ace is still counting as 11.</summary>
    public static (int Total, bool Soft) Total(IEnumerable<Card> cards)
    {
        var total = 0;
        var aces = 0;
        foreach (var card in cards)
        {
            if (card.Rank == Rank.Ace) { aces++; total += 11; }
            else total += card.Rank >= Rank.Ten ? 10 : (int)card.Rank;
        }
        while (total > 21 && aces > 0) { total -= 10; aces--; }
        return (total, aces > 0);
    }

    public static bool IsBlackjack(IReadOnlyCollection<Card> cards) => cards.Count == 2 && Total(cards).Total == 21;
}

/// <summary>
/// Blackjack against the house, for one to five players sharing a dealer. Each round:
/// everyone bets, two cards each (the dealer's second is face down), players act in seat
/// order (hit, stand, or double down on their first two cards), then the dealer draws to
/// 17 and bets settle. Blackjack pays 3:2, a win 1:1, a push returns the bet. After the
/// set number of rounds, the most chips wins; ties go to the earlier seat. No splitting
/// or insurance.
/// </summary>
public sealed class BlackjackGame
{
    public const int MinPlayers = 1;
    public const int MaxPlayers = 5;

    private readonly List<BjPlayer> _players = new();
    private readonly List<Card> _dealer = new();
    private readonly Func<Deck> _newDeck;
    private Deck _deck;
    private bool _holeRevealed;
    private bool _betting;

    public BlackjackGame(Func<Deck> newDeck, int startingChips = 500, int rounds = 10, int minBet = 10)
    {
        if (startingChips < minBet) throw new ArgumentOutOfRangeException(nameof(startingChips));
        if (rounds < 1) throw new ArgumentOutOfRangeException(nameof(rounds));
        if (minBet < 1) throw new ArgumentOutOfRangeException(nameof(minBet));

        _newDeck = newDeck;
        _deck = newDeck();
        StartingChips = startingChips;
        TotalRounds = rounds;
        MinBet = minBet;
    }

    public int StartingChips { get; }
    public int TotalRounds { get; }
    public int MinBet { get; }
    public int Round { get; private set; }
    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public string? WinnerId { get; private set; }

    public IReadOnlyList<BjPlayer> Players => _players;
    public IReadOnlyList<Card> DealerCards => _dealer;

    /// <summary>The dealer's face-up card once dealt.</summary>
    public Card? DealerUpCard => _dealer.Count > 0 ? _dealer[0] : null;

    public bool IsBetting => Phase == GamePhase.Playing && _betting;

    /// <summary>Seats that still owe a bet this round.</summary>
    public IEnumerable<BjPlayer> AwaitingBets =>
        IsBetting ? _players.Where(p => !p.Out && p.Bet == 0) : [];

    /// <summary>The seat whose hand is being played, in seat order.</summary>
    public BjPlayer? HandToPlay =>
        Phase == GamePhase.Playing && !_betting ? _players.FirstOrDefault(p => p.State == BjHandState.Playing) : null;

    public BjPlayer AddPlayer(string id, string name)
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("Players can only join while in the lobby.");
        if (_players.Count >= MaxPlayers)
            throw new InvalidOperationException($"A blackjack table seats at most {MaxPlayers}.");
        if (_players.Any(p => p.Id == id))
            throw new InvalidOperationException($"Player '{id}' is already at the table.");

        var player = new BjPlayer { Id = id, Name = name };
        _players.Add(player);
        return player;
    }

    /// <summary>A departing player forfeits any bet. The round carries on without them.</summary>
    public IReadOnlyList<BjEvent> RemovePlayer(string id)
    {
        var player = _players.FirstOrDefault(p => p.Id == id);
        if (player is null) return [];
        _players.Remove(player);
        if (Phase != GamePhase.Playing) return [];

        var events = new List<BjEvent>();
        if (_players.Count == 0)
        {
            Phase = GamePhase.GameOver;
            return events;
        }

        if (_betting) DealIfAllBetsAreIn(events);
        else FinishRoundIfNoHandsLeft(events);
        return events;
    }

    public IReadOnlyList<BjEvent> StartGame()
    {
        if (Phase != GamePhase.Lobby)
            throw new InvalidOperationException("The game has already started.");
        if (_players.Count < MinPlayers)
            throw new InvalidOperationException("Need at least one player to start.");

        foreach (var p in _players) p.Chips = StartingChips;
        Phase = GamePhase.Playing;

        var events = new List<BjEvent>();
        OpenBetting(events);
        return events;
    }

    public IReadOnlyList<BjEvent> PlaceBet(string playerId, int amount)
    {
        if (!IsBetting)
            throw new InvalidOperationException("Bets are closed.");
        var player = Find(playerId);
        if (player.Out)
            throw new InvalidOperationException("You're out of chips.");
        if (player.Bet > 0)
            throw new InvalidOperationException("You've already bet this round.");
        if (amount < MinBet)
            throw new InvalidOperationException($"The minimum bet is {MinBet}.");
        if (amount > player.Chips)
            throw new InvalidOperationException($"You only have {player.Chips} chips.");

        player.Chips -= amount;
        player.Bet = amount;

        var events = new List<BjEvent> { Event(BjEventKind.BetPlaced, playerId, amount) };
        DealIfAllBetsAreIn(events);
        return events;
    }

    public IReadOnlyList<BjEvent> Hit(string playerId)
    {
        var player = RequireTurn(playerId);
        var card = Draw();
        player.Cards.Add(card);

        var total = BjMath.Total(player.Cards).Total;
        if (total > 21) player.State = BjHandState.Bust;
        else if (total == 21) player.State = BjHandState.Stood; // nothing left to improve

        var events = new List<BjEvent> { Event(BjEventKind.Played, playerId, action: "hit", card: card.Code) };
        FinishRoundIfNoHandsLeft(events);
        return events;
    }

    public IReadOnlyList<BjEvent> Stand(string playerId)
    {
        var player = RequireTurn(playerId);
        player.State = BjHandState.Stood;

        var events = new List<BjEvent> { Event(BjEventKind.Played, playerId, action: "stand") };
        FinishRoundIfNoHandsLeft(events);
        return events;
    }

    /// <summary>Double the bet, take exactly one more card, and stand.</summary>
    public IReadOnlyList<BjEvent> DoubleDown(string playerId)
    {
        var player = RequireTurn(playerId);
        if (player.Cards.Count != 2)
            throw new InvalidOperationException("You can only double down on your first two cards.");
        if (player.Chips < player.Bet)
            throw new InvalidOperationException("You don't have the chips to double your bet.");

        player.Chips -= player.Bet;
        player.Bet *= 2;
        var card = Draw();
        player.Cards.Add(card);
        player.State = BjMath.Total(player.Cards).Total > 21 ? BjHandState.Bust : BjHandState.Doubled;

        var events = new List<BjEvent> { Event(BjEventKind.Played, playerId, player.Bet, "double", card.Code) };
        FinishRoundIfNoHandsLeft(events);
        return events;
    }

    public bool CanDouble(BjPlayer player) =>
        player.State == BjHandState.Playing && player.Cards.Count == 2 && player.Chips >= player.Bet;

    /// <summary>The table as the public may see it right now.</summary>
    public BjTable Snapshot()
    {
        var visible = _holeRevealed ? _dealer : _dealer.Take(1).ToList();
        var dealerCodes = _dealer.Select((c, i) => i == 1 && !_holeRevealed ? Card.HiddenCode : c.Code).ToArray();

        return new BjTable(
            Math.Min(Round, TotalRounds), TotalRounds, MinBet, IsBetting,
            HandToPlay?.Id,
            dealerCodes,
            BjMath.Total(visible).Total,
            _players.Select(p =>
            {
                var (total, soft) = BjMath.Total(p.Cards);
                return new BjSeat(p.Id, p.Chips, p.Bet, p.Cards.Select(c => c.Code).ToArray(), total, soft, p.State, p.Out);
            }).ToArray());
    }

    // ------------------------------------------------------------ round flow

    private void OpenBetting(List<BjEvent> events)
    {
        Round++;
        _dealer.Clear();
        _holeRevealed = false;
        foreach (var p in _players)
        {
            p.Cards.Clear();
            p.Bet = 0;
            p.State = BjHandState.Waiting;
            if (p.Chips < MinBet) p.Out = true;
        }

        if (Round > TotalRounds || _players.All(p => p.Out))
        {
            EndGame(events);
            return;
        }

        _betting = true;
        events.Add(Event(BjEventKind.BettingOpened));
    }

    private void DealIfAllBetsAreIn(List<BjEvent> events)
    {
        if (AwaitingBets.Any()) return;

        var inHand = _players.Where(p => p.Bet > 0).ToList();
        if (inHand.Count == 0)
        {
            // Everyone who could bet has left; nothing to deal.
            EndGame(events);
            return;
        }

        _betting = false;
        _deck = _newDeck();

        foreach (var p in inHand) p.Cards.Add(Draw());
        _dealer.Add(Draw());
        foreach (var p in inHand) p.Cards.Add(Draw());
        _dealer.Add(Draw());

        var dealerBlackjack = BjMath.IsBlackjack(_dealer);
        foreach (var p in inHand)
        {
            if (BjMath.IsBlackjack(p.Cards)) p.State = BjHandState.Blackjack;
            else p.State = dealerBlackjack ? BjHandState.Stood : BjHandState.Playing; // the dealer peeks: no point playing on
        }

        events.Add(Event(BjEventKind.Dealt));
        FinishRoundIfNoHandsLeft(events);
    }

    private void FinishRoundIfNoHandsLeft(List<BjEvent> events)
    {
        if (_betting || _players.Any(p => p.State == BjHandState.Playing)) return;

        _holeRevealed = true;
        events.Add(Event(BjEventKind.DealerPlayed, card: _dealer[1].Code, reveal: true));

        // The dealer only needs to draw if somebody's hand is still standing against them.
        var contested = _players.Any(p => p.State is BjHandState.Stood or BjHandState.Doubled);
        while (contested && DealerMustDraw())
        {
            var card = Draw();
            _dealer.Add(card);
            events.Add(Event(BjEventKind.DealerPlayed, card: card.Code));
        }

        Settle(events);
        OpenBetting(events);
    }

    /// <summary>The house draws to 17 and stands on all 17s, soft included.</summary>
    private bool DealerMustDraw() => BjMath.Total(_dealer).Total < 17;

    private void Settle(List<BjEvent> events)
    {
        var dealerTotal = BjMath.Total(_dealer).Total;
        var dealerBlackjack = BjMath.IsBlackjack(_dealer);
        var dealerBust = dealerTotal > 21;

        var payouts = new List<BjPayout>();
        foreach (var p in _players.Where(p => p.Bet > 0))
        {
            var total = BjMath.Total(p.Cards).Total;
            BjOutcome outcome;
            if (p.State == BjHandState.Bust) outcome = BjOutcome.Bust;
            else if (p.State == BjHandState.Blackjack) outcome = dealerBlackjack ? BjOutcome.Push : BjOutcome.Blackjack;
            else if (dealerBlackjack) outcome = BjOutcome.Lose;
            else if (dealerBust || total > dealerTotal) outcome = BjOutcome.Win;
            else if (total == dealerTotal) outcome = BjOutcome.Push;
            else outcome = BjOutcome.Lose;

            // The bet already left the player's stack, so "returned" is what comes back to it.
            var returned = outcome switch
            {
                BjOutcome.Blackjack => p.Bet + p.Bet * 3 / 2,
                BjOutcome.Win => p.Bet * 2,
                BjOutcome.Push => p.Bet,
                _ => 0,
            };
            p.Chips += returned;
            payouts.Add(new BjPayout(p.Id, outcome, returned - p.Bet));
        }

        events.Add(Event(BjEventKind.RoundSettled) with { Payouts = payouts.ToArray() });
    }

    private void EndGame(List<BjEvent> events)
    {
        Phase = GamePhase.GameOver;
        _betting = false;
        // Most chips wins; List order is seat order, and MaxBy keeps the first of equals.
        WinnerId = _players.MaxBy(p => p.Chips)?.Id;
        events.Add(Event(BjEventKind.GameOver) with { WinnerId = WinnerId });
    }

    // ----------------------------------------------------------------- helpers

    private BjEvent Event(BjEventKind kind, string? playerId = null, int amount = 0, string? action = null, string? card = null, bool reveal = false) =>
        new(kind, Snapshot(), playerId, amount, action, card, reveal);

    private Card Draw()
    {
        if (_deck.Remaining == 0) _deck = _newDeck(); // can't happen with five seats, but never deal from air
        return _deck.Draw();
    }

    private BjPlayer Find(string playerId) =>
        _players.FirstOrDefault(p => p.Id == playerId)
        ?? throw new InvalidOperationException("You're not seated at this table.");

    private BjPlayer RequireTurn(string playerId)
    {
        if (Phase != GamePhase.Playing || _betting)
            throw new InvalidOperationException("There's no hand to play right now.");
        var player = Find(playerId);
        if (HandToPlay?.Id != playerId)
            throw new InvalidOperationException("It is not your turn.");
        return player;
    }
}
