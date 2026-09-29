using System.Numerics;
using TavernGames.Core.Protocol;

namespace TavernGames.Plugin.Game;

/// <summary>The word on your seat's display strip, and the colour it glows: YOUR TURN, LIAR!, SHOWDOWN.</summary>
public readonly record struct DisplayState(string Word, Vector4 Colour);

/// <summary>
/// The plugin-side half of a tavern game: it tracks that game's messages, words its log and
/// chat narration, and draws its table. The shell (<see cref="GameSession"/> and the main
/// window) handles everything games share: connecting, rooms, seats, bots, spectating.
/// <para>
/// A game draws in two places, and the split is the same one the server makes. The felt is
/// what everyone at the table sees, so it gets only what the server sent to all. Your seat is
/// what only you see and what you can do: your dice or cards, and the keys. A spectator gets a
/// felt and no seat.
/// </para>
/// </summary>
public interface IClientGame
{
    /// <summary>Matches the server module's wire id, e.g. "liarsdice".</summary>
    string GameType { get; }

    /// <summary>The seat on the clock, for the lit plate. Empty when unknown.</summary>
    string CurrentPlayerId { get; }

    void Reset();

    /// <summary>Applies one of this game's messages. Returns false if the message isn't this game's.</summary>
    bool Apply(NetMessage message, GameSession session);

    /// <summary>An in-character chat line for the message, or null to stay quiet.</summary>
    string? Narrate(NetMessage message, GameSession session);

    /// <summary>What your seat's display strip says right now. Only asked for seated players.</summary>
    DisplayState Display(GameSession session);

    /// <summary>What everyone sees: the seats and the middle of the table. Drawn on the felt.</summary>
    void DrawFelt(GameSession session);

    /// <summary>What only you see and what you can do. Drawn on your seat; never for a spectator.</summary>
    void DrawSeat(GameSession session, Action<NetMessage> send);

    /// <summary>One seat's tally on its plate (hidden dice, a score bar, chips...).</summary>
    void DrawSeatTally(PlayerPublic seat);
}
