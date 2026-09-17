using TavernGames.Server.Data;

namespace TavernGames.Server;

/// <summary>
/// Saying hello without a token creates a profile, so that a new player never has to do
/// anything to get one. The cost is that every throwaway connection leaves a row behind.
/// This sweeps them: a profile that joined nothing, played nothing and has not been seen
/// for a month is deleted. Runs at startup and then daily.
/// </summary>
public sealed class CommunityJanitor(CommunityStore store, ILogger<CommunityJanitor> log) : BackgroundService
{
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                var removed = store.PurgeAbandonedProfiles(DateTime.UtcNow - AbandonedAfter);
                if (removed > 0) log.LogInformation("Removed {Count} abandoned profiles", removed);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Profile cleanup failed; it will be retried tomorrow");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
