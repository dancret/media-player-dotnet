using System.Net;
using Microsoft.Extensions.Logging;

namespace MediaPlayer.NetCord.Misc;

/// <summary>
/// Represents a gate that ensures network readiness by performing DNS and HTTPS checks.
/// </summary>
public sealed class NetworkGate(ILogger<NetworkGate> logger, HttpClient http)
{
    /// <summary>
    /// Waits until the network is ready by performing DNS and HTTPS checks.
    /// </summary>
    /// <param name="ct">
    /// A <see cref="CancellationToken"/> that can be used to cancel the operation.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> representing the asynchronous operation.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown if the HTTPS check fails.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is canceled via the <paramref name="ct"/>.
    /// </exception>
    public async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        logger.LogInformation("Checking network readiness (DNS + HTTPS)...");

        _ = await Dns.GetHostEntryAsync("discord.com", ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://discord.com");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        resp.EnsureSuccessStatusCode();
    }
}