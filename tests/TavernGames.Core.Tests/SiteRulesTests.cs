using System.Net;
using System.Text;
using TavernGames.Core.Platform;

namespace TavernGames.Core.Tests;

/// <summary>
/// The site's "How to play" is generated from the same rules the plugin shows, so the two can't
/// drift. This test renders the block and compares it with docs/index.html. Run the Core tests
/// with TAVERN_UPDATE_SITE=1 to write the fresh block into the page instead, then commit it.
/// </summary>
public class SiteRulesTests
{
    private const string StartMarker =
        "<!-- rules:start: generated from each game's rules by SiteRulesTests. Run the Core tests with TAVERN_UPDATE_SITE=1 to refresh. -->";
    private const string EndMarker = "<!-- rules:end -->";
    private const string Indent = "    ";

    [Fact]
    public void TheSite_ShowsTheSameRulesAsThePlugin()
    {
        var path = Path.Combine(RepoRoot(), "docs", "index.html");
        var raw = File.ReadAllText(path);
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var page = raw.Replace("\r\n", "\n");

        var start = page.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = page.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "docs/index.html has lost its rules:start and rules:end markers.");

        var blockStart = page.LastIndexOf('\n', start) + 1;
        var blockEnd = end + EndMarker.Length;
        var current = page[blockStart..blockEnd];
        var expected = Render();
        if (current == expected)
            return;

        if (Environment.GetEnvironmentVariable("TAVERN_UPDATE_SITE") == "1")
        {
            var updated = page[..blockStart] + expected + page[blockEnd..];
            File.WriteAllText(path, updated.Replace("\n", newline), new UTF8Encoding(false));
            return;
        }

        Assert.Fail("docs/index.html's How to play is out of date with the games' rules. " +
                    "Run the Core tests with TAVERN_UPDATE_SITE=1 to regenerate it, then commit the page.");
    }

    /// <summary>One collapsible block per game, in the catalog's order, which is also the plugin's tile order.</summary>
    private static string Render()
    {
        var sb = new StringBuilder();
        sb.Append(Indent).Append(StartMarker).Append('\n');
        foreach (var game in GameCatalog.Games)
        {
            sb.Append(Indent).Append($"<details class=\"rules\" id=\"rules-{game.Type}\">\n");
            sb.Append(Indent).Append($"  <summary><span class=\"game\">{Html(game.DisplayName)}</span><span class=\"seats\">{Seats(game)}</span></summary>\n");
            sb.Append(Indent).Append("  <div class=\"body\">\n");
            foreach (var section in game.Rules)
            {
                sb.Append(Indent).Append($"    <h3>{Html(section.Heading)}</h3>\n");
                sb.Append(Indent).Append($"    <p>{Html(section.Text)}</p>\n");
            }
            sb.Append(Indent).Append("  </div>\n");
            sb.Append(Indent).Append("</details>\n");
        }
        sb.Append(Indent).Append(EndMarker);
        return sb.ToString();
    }

    private static string Seats(GameDescriptor game) =>
        game.MinPlayers == game.MaxPlayers ? $"{game.MinPlayers} players" : $"{game.MinPlayers} to {game.MaxPlayers} players";

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TavernGames.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not find the repository root (TavernGames.slnx) above the test output folder.");
    }
}
