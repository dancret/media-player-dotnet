using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;

namespace MediaPlayer.NetCord.Misc;

public static class ExtensionMethods
{
    /// <summary>
    /// Waits for the network to become ready before proceeding with the application startup.
    /// </summary>
    /// <param name="host">
    /// The <see cref="IHost"/> instance representing the application host.
    /// </param>
    /// <param name="ct">
    /// A <see cref="CancellationToken"/> to observe while waiting for the network.
    /// </param>
    /// <param name="backoff">
    /// An optional function to calculate the delay between retry attempts. 
    /// Defaults to an exponential backoff strategy capped at 60 seconds.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> that represents the asynchronous operation.
    /// </returns>
    /// <remarks>
    /// This method uses a retry policy to handle network-related exceptions and logs warnings during retries.
    /// </remarks>
    public static async Task WaitForNetworkAsync(
        this IHost host,
        CancellationToken ct,
        Func<int, TimeSpan>? backoff = null)
    {
        using var scope = host.Services.CreateScope();

        var gate = scope.ServiceProvider.GetRequiredService<NetworkGate>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("NetworkStartupGate");

        // Calculates delay as 2^attempt seconds, capped at 60 seconds and attempt <= 6.
        backoff ??= attempt =>
            TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));

        var policy = Policy
            .Handle<Exception>(ex => ex is not OperationCanceledException) // Excluding OperationCanceledException avoids retries during shutdown cancellation
            .WaitAndRetryForeverAsync(
                retryAttempt => backoff(retryAttempt),
                (exception, retryAttempt, delay) =>
                {
                    logger.LogWarning(exception, "Network not ready. Retrying in {Delay} (attempt {Attempt})", delay, retryAttempt);
                });

        await policy.ExecuteAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            await gate.WaitUntilReadyAsync(token);
        }, ct);
    }

    /// <summary>
    /// Adds the <see cref="NetworkGate"/> service to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to which the service will be added.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    public static IServiceCollection AddNetworkGate(this IServiceCollection services)
    {
        services.AddHttpClient<NetworkGate>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        }).RemoveAllLoggers();

        return services;
    }
}