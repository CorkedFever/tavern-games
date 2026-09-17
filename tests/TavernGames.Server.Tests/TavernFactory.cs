using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace TavernGames.Server.Tests;

/// <summary>
/// Hosts the real server in-process with instant bots and its own throwaway database
/// (under the test output folder), so test classes never share profiles or venues.
/// </summary>
public sealed class TavernFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = NewDbPath();

    public TavernFactory() => Environment.SetEnvironmentVariable("TAVERN_BOT_DELAY_MS", "0");

    public static string NewDbPath() =>
        Path.Combine(AppContext.BaseDirectory, "testdata", $"tavern-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Tavern:DbPath", _dbPath);

    public async Task<WsTestClient> ConnectAsync()
    {
        var wsUri = new UriBuilder(new Uri(Server.BaseAddress, "ws")) { Scheme = "ws" }.Uri;
        var socket = await Server.CreateWebSocketClient().ConnectAsync(wsUri, CancellationToken.None);
        return new WsTestClient(socket);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        DeleteDb(_dbPath);
    }

    public static void DeleteDb(string path)
    {
        SqliteConnection.ClearAllPools(); // release the file handle before deleting on Windows
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}
