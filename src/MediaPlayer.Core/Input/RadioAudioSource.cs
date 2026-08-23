using MediaPlayer.Ffmpeg;
using MediaPlayer.Tracks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaPlayer.Input;

/// <summary>
/// Audio source that reads direct radio stream URLs using ffmpeg to decode into PCM.
/// </summary>
public sealed class RadioAudioSource(
    ILogger<RadioAudioSource> logger,
    IOptions<FfmpegOptions> options) : IAudioSource
{
    private readonly FfmpegOptions _options = options.Value;
    private readonly ILogger<RadioAudioSource> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    public Task<IAudioTrackReader> OpenReaderAsync(Track track, CancellationToken ct)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));
        if (string.IsNullOrWhiteSpace(track.Uri))
            throw new ArgumentException("Radio stream URL must not be empty.", nameof(track));

        var reader = FfmpegPcmSource.StartPcmReader(track.Uri, _options, _logger, ct);
        return Task.FromResult(reader);
    }
}
