namespace TavernGames.Core.Protocol;

/// <summary>
/// Public, non-secret view of a seat. <see cref="Tally"/> is whatever single number
/// the current game tracks per player: dice left in Liar's Dice, banked score in Pig.
/// </summary>
public sealed record PlayerPublic(string Id, string Name, int Tally, bool Eliminated, bool IsBot = false);
