using MediaPlayer.Output;
using Microsoft.Extensions.Logging;
using NetCord.Gateway.Voice;

namespace MediaPlayer.NetCord.Player;

/// <summary>
/// Audio sink that writes raw PCM data into a Discord voice connection.
/// </summary>
/// <remarks>
/// This sink is intended to be used by the MediaPlayer playback pipeline.
/// It assumes the input is 48 kHz, 16-bit PCM stereo, which is what Discord
/// expects when using <see cref="VoiceClient.CreateOutputStream"/>.
///
/// The same <see cref="OpusEncodeStream"/> instance is reused across tracks
/// within a voice session. Individual tracks are delimited by calls to
/// <see cref="CompleteAsync"/>; for now we simply flush the stream.
/// </remarks>
internal sealed class NetCordAudioSink(
    VoiceClient voiceClient,
    ILogger<NetCordAudioSink> logger)
    : IAudioSink
{
    private readonly Lock _sync = new();
    private OpusEncodeStream? _opusStream;
    private bool _disposed;

    // PCM constants (48kHz / stereo / 16-bit)
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BytesPerSample = 2;
    private const int BytesPerSecond = SampleRate * Channels * BytesPerSample; // 192_000

    // Pacing state is kept using these fields.
    // The opus stream has no back pressure, so any pause-resume will 
    // just fast-forward the audio.
    private long _bytesSent;
    private DateTimeOffset _clockStart;
    private DateTimeOffset _lastWriteUtc;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NetCordAudioSink));

        if (buffer.Length == 0)
            return;

        var stream = EnsureStreamCreated();

        var now = DateTimeOffset.UtcNow;

        // If this is the first write, or there was a gap (pause / stall),
        // reset our audio clock so we don't try to "catch up" old audio.
        if (_clockStart == default || (now - _lastWriteUtc) > TimeSpan.FromSeconds(1))
        {
            _clockStart = now;
            _bytesSent = 0;
        }

        _lastWriteUtc = now;

        try
        {
            // Feed PCM into NetCord's Opus encoder / output stream
            await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "NetCordAudioSink.WriteAsync failed (size={Size})",
                buffer.Length);
            throw;
        }

        // Real-time pacing for Discord
        _bytesSent += buffer.Length;

        // How much "audio time" have we sent in this contiguous run?
        var expectedMs = (double)_bytesSent * 1000.0 / BytesPerSecond;
        var targetTime = _clockStart + TimeSpan.FromMilliseconds(expectedMs);

        now = DateTimeOffset.UtcNow;
        var delay = targetTime - now;

        // If we're ahead of real time, delay a bit (but don't oversleep).
        if (delay > TimeSpan.Zero && delay < TimeSpan.FromSeconds(2))
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Let cancellation propagate via ct on the next await
            }
        }
    }

    public async ValueTask CompleteAsync(CancellationToken ct)
    {
        if (_disposed)
            return;

        if (_opusStream is null)
        {
            logger.LogDebug("NetCordAudioSink.CompleteAsync called but stream was null.");
            return;
        }

        try
        {
            await _opusStream.FlushAsync(ct).ConfigureAwait(false);
            logger.LogDebug("NetCordAudioSink: Track flush completed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "NetCordAudioSink.FlushAsync failed.");
        }
        finally
        {
            // Reset pacing after each track so the next one starts fresh.
            _clockStart = default;
            _bytesSent = 0;
            _lastWriteUtc = default;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        var stream = _opusStream;
        _opusStream = null;

        if (stream != null)
        {
            try
            {
                stream.Dispose();
                logger.LogDebug("NetCordAudioSink: PCM stream disposed.");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "NetCordAudioSink: Error disposing stream.");
            }
        }

        logger.LogDebug("NetCordAudioSink: Disposed.");
        return ValueTask.CompletedTask;
    }

    private Stream EnsureStreamCreated()
    {
        var existing = _opusStream;
        if (existing is not null)
            return existing;

        lock (_sync)
        {
            if (_opusStream is not null)
                return _opusStream;

            var outStream = voiceClient.CreateOutputStream();

            _opusStream = new OpusEncodeStream(
                outStream,
                PcmFormat.Short,
                VoiceChannels.Stereo,
                OpusApplication.Audio);

            logger.LogDebug("NetCordAudioSink: Created OpusEncodeStream.");

            // Reset pacing when a new underlying stream is created
            _clockStart = default;
            _bytesSent = 0;
            _lastWriteUtc = default;

            return _opusStream;
        }
    }
}
