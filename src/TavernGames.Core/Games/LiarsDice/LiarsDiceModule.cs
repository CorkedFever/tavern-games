using TavernGames.Core.Platform;
using TavernGames.Core.Protocol;

namespace TavernGames.Core.Games.LiarsDice;

/// <summary>Adapts the <see cref="LiarsDiceGame"/> engine to the tavern platform.</summary>
public sealed class LiarsDiceModule : IGameModule
{
    public const string Type = "liarsdice";
    private const string StartingDiceKey = "startingDice";

    public static GameDescriptor Descriptor { get; } = new(
        Type: Type,
        DisplayName: "Liar's Dice",
        Blurb: "Roll in secret, bid on the whole table's dice, and call out the liar.",
        Rules: LiarsDiceRules.Sections,
        MinPlayers: LiarsDiceGame.MinPlayers,
        MaxPlayers: LiarsDiceGame.MaxPlayers,
        Options: [new GameOption(StartingDiceKey, "Dice per player", 1, 6, LiarsDiceGame.DefaultStartingDice, "How many dice each player starts with, from 1 to 6. The default is 5.")],
        Messages:
        [
            (typeof(PlaceBid), "liarsdice.placeBid"),
            (typeof(Challenge), "liarsdice.challenge"),
            (typeof(RoundStarted), "liarsdice.roundStarted"),
            (typeof(YourHand), "liarsdice.yourHand"),
            (typeof(BidPlaced), "liarsdice.bidPlaced"),
            (typeof(ChallengeResolved), "liarsdice.challengeResolved"),
        ],
        Create: options => new LiarsDiceModule(new RandomDiceRoller(), options[StartingDiceKey]));

    private readonly LiarsDiceGame _game;
    private readonly HashSet<string> _bots = new();

    public LiarsDiceModule(IDiceRoller roller, int startingDice) =>
        _game = new LiarsDiceGame(roller, startingDice);

    public string GameType => Type;
    public int MinPlayers => LiarsDiceGame.MinPlayers;
    public int MaxPlayers => LiarsDiceGame.MaxPlayers;
    public GamePhase Phase => _game.Phase;

    public string? CurrentActorId =>
        _game.Phase == GamePhase.Playing ? _game.Current.Id : null;

    public PlayerPublic[] Roster() =>
        _game.Players
            .Select(p => new PlayerPublic(p.Id, p.Name, p.DiceCount, p.IsEliminated, _bots.Contains(p.Id)))
            .ToArray();

    public void AddPlayer(string id, string name, bool isBot)
    {
        _game.AddPlayer(id, name);
        if (isBot) _bots.Add(id);
    }

    public IReadOnlyList<Emit> RemovePlayer(string id)
    {
        var wasPlaying = _game.Phase == GamePhase.Playing;
        _game.RemovePlayer(id);
        _bots.Remove(id);

        if (!wasPlaying) return [];
        if (_game.Phase == GamePhase.GameOver)
            return [new ToAll(new GameEnded(_game.WinnerId ?? "", Roster()))];

        // Re-announce whose turn it is now that a seat is gone.
        return [new ToAll(new RoundStarted(_game.Current.Id, Roster()))];
    }

    public IReadOnlyList<Emit> Start()
    {
        _game.StartGame();
        return RoundEmits();
    }

    public IReadOnlyList<Emit> Handle(string playerId, NetMessage move) => move switch
    {
        PlaceBid bid => ApplyBid(playerId, new Bid(bid.Quantity, bid.FaceValue)),
        Challenge => ApplyChallenge(playerId),
        _ => throw new InvalidOperationException($"'{move.GetType().Name}' is not a Liar's Dice move."),
    };

    public NetMessage? DecideBotMove(string botId, Random rng)
    {
        var self = _game.Players.FirstOrDefault(p => p.Id == botId);
        if (self is null || _game.Phase != GamePhase.Playing) return null;

        var decision = BotStrategy.Decide(_game, self, rng);
        if (!decision.IsChallenge)
            return new PlaceBid(decision.Bid.Quantity, decision.Bid.FaceValue);
        return _game.CurrentBid is null ? null : new Challenge();
    }

    public IReadOnlyList<NetMessage> CatchUp()
    {
        if (_game.Phase != GamePhase.Playing) return [];

        var messages = new List<NetMessage> { new RoundStarted(_game.Current.Id, Roster()) };
        if (_game.CurrentBid is { } bid && _game.CurrentBidderId is { } bidder)
            messages.Add(new BidPlaced(bidder, BidDto.From(bid), _game.Current.Id));
        return messages;
    }

    private IReadOnlyList<Emit> ApplyBid(string playerId, Bid bid)
    {
        _game.PlaceBid(playerId, bid);
        return [new ToAll(new BidPlaced(playerId, BidDto.From(bid), _game.Current.Id))];
    }

    private IReadOnlyList<Emit> ApplyChallenge(string playerId)
    {
        var outcome = _game.Challenge(playerId);

        var emits = new List<Emit>
        {
            new ToAll(new ChallengeResolved(
                Bid: BidDto.From(outcome.Bid),
                ChallengerId: outcome.ChallengerId,
                BidderId: outcome.BidderId,
                ActualCount: outcome.ActualCount,
                BidWasValid: outcome.BidWasValid,
                LoserId: outcome.LoserId,
                LoserEliminated: outcome.LoserEliminated,
                Reveal: outcome.RevealedHands.Select(kv => new HandReveal(kv.Key, kv.Value.ToArray())).ToArray(),
                GameOver: outcome.GameOver,
                WinnerId: outcome.WinnerId,
                NextStarterId: outcome.NextStarterId)),
        };

        if (outcome.GameOver)
        {
            emits.Add(new ToAll(new GameEnded(outcome.WinnerId ?? "", Roster())));
        }
        else
        {
            // Let everyone read the reveal before the dice re-roll.
            emits.Add(new Pause(PauseKind.RoundBreak));
            emits.AddRange(RoundEmits());
        }
        return emits;
    }

    /// <summary>Each seat's private hand first, then the public round state.</summary>
    private List<Emit> RoundEmits()
    {
        var emits = new List<Emit>();
        foreach (var p in _game.Players)
            emits.Add(new ToPlayer(p.Id, new YourHand(p.Dice.ToArray())));
        emits.Add(new ToAll(new RoundStarted(_game.Current.Id, Roster())));
        return emits;
    }
}
