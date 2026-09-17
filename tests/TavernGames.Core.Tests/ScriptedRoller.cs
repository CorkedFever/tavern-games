using TavernGames.Core;
using TavernGames.Core.Games.LiarsDice;

namespace TavernGames.Core.Tests;

/// <summary>Deterministic roller that returns a fixed, repeating sequence of faces.</summary>
public sealed class ScriptedRoller(params int[] faces) : IDiceRoller
{
    private int _i;
    public int Roll() => faces[_i++ % faces.Length];
}
