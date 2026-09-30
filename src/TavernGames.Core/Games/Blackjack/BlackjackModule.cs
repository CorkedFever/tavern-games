using TavernGames.Core.Cards;
using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.Blackjack;

/// <summary>
/// A sensible, slightly cautious player: textbook hit/stand decisions against the
/// dealer's up card, doubles on the classic spots, and bets about a tenth of its stack.
/// </summary>
public static class BlackjackBot
{
    public static int ChooseBet(BlackjackGame game, BjPlayer self, Random rng)
    {
        var target = self.Chips / 10 + rng.Next(-1, 2) * 5;
        var rounded = Math.Max(5, target / 5 * 5);
        return Math.Clamp(rounded, game.MinBet, self.Chips);
    }

    public static NetMessage ChoosePlay(BlackjackGame game, BjPlayer self)
    {
        var (total, soft) = BjMath.Total(self.Cards);
        var up = game.DealerUpCard is { } card ? UpValue(card) : 10;

        if (game.CanDouble(self) && !soft)
        {
            if (total == 11) return new BjDouble();
            if (total == 10 && up <= 9) return new BjDouble();
            if (total == 9 && up is >= 3 and <= 6) return new BjDouble();
        }

        if (soft)
        {
            if (total <= 17) return new BjHit();
            if (total == 18) return up >= 9 ? new BjHit() : new BjStand();
            return new BjStand();
        }

        if (total <= 11) return new BjHit();
        if (total == 12) return up is >= 4 and <= 6 ? new BjStand() : new BjHit();
        if (total <= 16) return up <= 6 ? new BjStand() : new BjHit();
        return new BjStand();
    }

    private static int UpValue(Card card) =>
        card.Rank == Rank.Ace ? 11 : card.Rank >= Rank.Ten ? 10 : (int)card.Rank;
}

/// <summary>Adapts the <see cref="BlackjackGame"/> engine to the tavern platform.</summary>
public sealed class BlackjackModule : IGameModule
{
    public const string Type = "blackjack";
    private const string ChipsKey = "startingChips";
    private const string RoundsKey = "rounds";
    private const string MinBetKey = "minBet";

    public static GameDescriptor Descriptor { get; } = new(
        Type: Type,
        DisplayName: "Blackjack",
        Blurb: "Beat the dealer to 21 without going over. Most chips after the last round wins.",
        Rules: BlackjackRules.Sections,
        MinPlayers: BlackjackGame.MinPlayers,
        MaxPlayers: BlackjackGame.MaxPlayers,
        Options:
        [
            new GameOption(ChipsKey, "Starting chips", 100, 5000, 500, "How many chips each player starts with. The default is 500."),
            new GameOption(RoundsKey, "Rounds", 3, 30, 10, "How many rounds are dealt before the game ends. The default is 10."),
            new GameOption(MinBetKey, "Minimum bet", 5, 100, 10, "The smallest bet anyone can place, and a player who can't cover it is out. The default is 10."),
        ],
        Messages:
        [
            (typeof(BjBet), "blackjack.bet"),
            (typeof(BjHit), "blackjack.hit"),
            (typeof(BjStand), "blackjack.stand"),
            (typeof(BjDouble), "blackjack.double"),
            (typeof(BjBettingOpened), "blackjack.bettingOpened"),
            (typeof(BjBetPlaced), "blackjack.betPlaced"),
            (typeof(BjDealt), "blackjack.dealt"),
            (typeof(BjPlayed), "blackjack.played"),
            (typeof(BjDealerPlayed), "blackjack.dealerPlayed"),
            (typeof(BjRoundSettled), "blackjack.roundSettled"),
            (typeof(BjSnapshot), "blackjack.snapshot"),
        ],
        Create: options => new BlackjackModule(
            Deck.Shuffled, options[ChipsKey], options[RoundsKey], Math.Min(options[MinBetKey], options[ChipsKey])));

    private readonly BlackjackGame _game;
    private readonly HashSet<string> _bots = new();

    public BlackjackModule(Func<Deck> newDeck, int startingChips, int rounds, int minBet) =>
        _game = new BlackjackGame(newDeck, startingChips, rounds, minBet);

    public string GameType => Type;
    public int MinPlayers => BlackjackGame.MinPlayers;
    public int MaxPlayers => BlackjackGame.MaxPlayers;
    public GamePhase Phase => _game.Phase;

    /// <summary>
    /// During betting everyone acts at once, so "the seat on the clock" is whichever bot
    /// still owes a bet (bots never wait on a human), or failing that the first human.
    /// </summary>
    public string? CurrentActorId
    {
        get
        {
            if (_game.Phase != GamePhase.Playing) return null;
            if (!_game.IsBetting) return _game.HandToPlay?.Id;
            var pending = _game.AwaitingBets.ToList();
            return (pending.FirstOrDefault(p => _bots.Contains(p.Id)) ?? pending.FirstOrDefault())?.Id;
        }
    }

    public PlayerPublic[] Roster() =>
        _game.Players
            .Select(p => new PlayerPublic(p.Id, p.Name, p.Chips, p.Out, _bots.Contains(p.Id)))
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
            emits.Add(new ToAll(new BjSnapshot(_game.Snapshot()))); // show the table without the departed seat
        return emits;
    }

    public IReadOnlyList<Emit> Start() => ToEmits(_game.StartGame());

    public IReadOnlyList<Emit> Handle(string playerId, NetMessage move) => ToEmits(move switch
    {
        BjBet bet => _game.PlaceBet(playerId, bet.Amount),
        BjHit => _game.Hit(playerId),
        BjStand => _game.Stand(playerId),
        BjDouble => _game.DoubleDown(playerId),
        _ => throw new InvalidOperationException($"'{move.GetType().Name}' is not a blackjack move."),
    });

    public NetMessage? DecideBotMove(string botId, Random rng)
    {
        var self = _game.Players.FirstOrDefault(p => p.Id == botId);
        if (self is null || _game.Phase != GamePhase.Playing) return null;

        if (_game.IsBetting)
            return self.Bet == 0 && !self.Out ? new BjBet(BlackjackBot.ChooseBet(_game, self, rng)) : null;
        return _game.HandToPlay?.Id == botId ? BlackjackBot.ChoosePlay(_game, self) : null;
    }

    public IReadOnlyList<NetMessage> CatchUp() =>
        _game.Phase == GamePhase.Playing ? [new BjSnapshot(_game.Snapshot())] : [];

    /// <summary>Turns the engine's chain of events into messages, with pauses where a table would naturally breathe.</summary>
    private List<Emit> ToEmits(IReadOnlyList<BjEvent> events)
    {
        var emits = new List<Emit>();
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case BjEventKind.BettingOpened:
                    emits.Add(new ToAll(new BjBettingOpened(e.Table)));
                    break;
                case BjEventKind.BetPlaced:
                    emits.Add(new ToAll(new BjBetPlaced(e.PlayerId!, e.Amount, e.Table)));
                    break;
                case BjEventKind.Dealt:
                    emits.Add(new ToAll(new BjDealt(e.Table)));
                    break;
                case BjEventKind.Played:
                    emits.Add(new ToAll(new BjPlayed(e.PlayerId!, e.Action!, e.Card, e.Table)));
                    break;
                case BjEventKind.DealerPlayed:
                    emits.Add(new Pause(PauseKind.Beat)); // one card at a time, like a real dealer
                    emits.Add(new ToAll(new BjDealerPlayed(e.Reveal, e.Card!, e.Table)));
                    break;
                case BjEventKind.RoundSettled:
                    emits.Add(new Pause(PauseKind.Beat));
                    emits.Add(new ToAll(new BjRoundSettled(e.Payouts ?? [], e.Table)));
                    emits.Add(new Pause(PauseKind.RoundBreak)); // time to read who won what
                    break;
                case BjEventKind.GameOver:
                    emits.Add(new ToAll(new GameEnded(e.WinnerId ?? "", Roster())));
                    break;
            }
        }
        return emits;
    }
}
