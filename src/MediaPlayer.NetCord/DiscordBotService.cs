using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaPlayer.NetCord;

public sealed class DiscordBotService(
    ILogger<DiscordBotService> logger,
    IHostApplicationLifetime lifetime)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Discord bot starting...");
        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Discord bot stopping...");
        return Task.CompletedTask;
    }
}