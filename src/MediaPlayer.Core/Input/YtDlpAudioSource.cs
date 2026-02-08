using MediaPlayer.Tracks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;

namespace MediaPlayer.Input;

/// <summary>
/// Audio source that reads yt-dlp output and pipes it into ffmpeg to decode into PCM.
/// </summary>
public sealed class YtDlpAudioSource : IAudioSource
{
    private readonly ILogger<YtDlpAudioSource> _logger;
    private readonly Func<Track, string> _urlSelector;
    private readonly YtDlpOptions _ytDlpOptions;
    private readonly FfmpegOptions _ffmpegOptions;

    /// <summary>
    /// Creates a new <see cref="YtDlpAudioSource"/>.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ytDlpOptions">Options for configuring yt-dlp.</param>
    /// <param name="ffmpegOptions">Options for configuring ffmpeg.</param>
    /// <param name="urlSelector">
    /// Given a <see cref="Track"/>, returns the YouTube (or supported site) URL
    /// that should be passed to yt-dlp.
    /// </param>
    /// <exception cref="ArgumentNullException"></exception>
    public YtDlpAudioSource(
        ILogger<YtDlpAudioSource> logger,
        IOptions<YtDlpOptions> ytDlpOptions,
        IOptions<FfmpegOptions> ffmpegOptions,
        Func<Track, string>? urlSelector = null)
    {
        _logger = logger;
        _urlSelector = urlSelector ?? (static track => track.Uri);
        _ytDlpOptions = ytDlpOptions.Value;
        _ffmpegOptions = ffmpegOptions.Value;
    }

    /// <inheritdoc/>
    public Task<IAudioTrackReader> OpenReaderAsync(Track track, CancellationToken ct)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));

        var url = _urlSelector(track);
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL selector returned null or empty.", nameof(track));

        // Start yt-dlp: download/stream bestaudio to stdout
        var ytdlpPsi = new ProcessStartInfo
        {
            FileName = _ytDlpOptions.YtDlpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // yt-dlp -f bestaudio/best -o - --no-playlist --no-progress <url>
        ytdlpPsi.ArgumentList.Add("-f");
        ytdlpPsi.ArgumentList.Add("bestaudio/best");
        ytdlpPsi.ArgumentList.Add("-o");
        ytdlpPsi.ArgumentList.Add("-");
        ytdlpPsi.ArgumentList.Add("--no-playlist");
        ytdlpPsi.ArgumentList.Add("--no-progress");   // remove/comment if you need progress bar for troubleshooting
        ytdlpPsi.ArgumentList.Add(url);

        if (_ytDlpOptions.UseCookies)
        {
            if (!string.IsNullOrWhiteSpace(_ytDlpOptions.CookiesFromBrowser))
            {
                ytdlpPsi.ArgumentList.Add("--cookies-from-browser");
                ytdlpPsi.ArgumentList.Add(_ytDlpOptions.CookiesFromBrowser);
            }

            if (!string.IsNullOrWhiteSpace(_ytDlpOptions.CookiesFile) && File.Exists(_ytDlpOptions.CookiesFile))
            {
                ytdlpPsi.ArgumentList.Add("--cookies");
                ytdlpPsi.ArgumentList.Add(_ytDlpOptions.CookiesFile);
            }
        }

        var ytdlp = new Process
        {
            StartInfo = ytdlpPsi,
            EnableRaisingEvents = true
        };

        if (!ytdlp.Start())
        {
            ytdlp.Dispose();
            throw new InvalidOperationException($"Failed to start '{_ytDlpOptions.YtDlpPath}'.");
        }

        // Start ffmpeg: read container from stdin, output raw PCM to stdout
        var ffmpegPsi = new ProcessStartInfo
        {
            FileName = _ffmpegOptions.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (_ffmpegOptions.HideBanner)
        {
            ffmpegPsi.ArgumentList.Add("-hide_banner");
        }

        ffmpegPsi.ArgumentList.Add("-loglevel");
        ffmpegPsi.ArgumentList.Add(_ffmpegOptions.LogLevel);

        // Input from stdin
        ffmpegPsi.ArgumentList.Add("-i");
        ffmpegPsi.ArgumentList.Add("-");

        // Audio-only, raw PCM s16le stereo 48k
        ffmpegPsi.ArgumentList.Add("-vn");
        ffmpegPsi.ArgumentList.Add("-f");
        ffmpegPsi.ArgumentList.Add(_ffmpegOptions.SampleFormat);
        ffmpegPsi.ArgumentList.Add("-ac");
        ffmpegPsi.ArgumentList.Add(_ffmpegOptions.Channels.ToString(CultureInfo.InvariantCulture));
        ffmpegPsi.ArgumentList.Add("-ar");
        ffmpegPsi.ArgumentList.Add(_ffmpegOptions.SampleRate.ToString(CultureInfo.InvariantCulture));
        ffmpegPsi.ArgumentList.Add("pipe:1");

        var ffmpeg = new Process
        {
            StartInfo = ffmpegPsi,
            EnableRaisingEvents = true
        };

        if (!ffmpeg.Start())
        {
            try
            {
                ytdlp.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill {YtDlpPath} process.", _ytDlpOptions.YtDlpPath);
            }
            ytdlp.Dispose();
            ffmpeg.Dispose();
            throw new InvalidOperationException($"Failed to start '{_ffmpegOptions.FfmpegPath}'.");
        }

        var ytdlpOut = ytdlp.StandardOutput.BaseStream;
        var ffmpegIn = ffmpeg.StandardInput.BaseStream;

        // Pump yt-dlp stdout into ffmpeg stdin in the background
        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpTask = Task.Run(async () =>
        {
            var buffer = new byte[81920];
            try
            {
                while (!pumpCts.IsCancellationRequested)
                {
                    var read = await ytdlpOut.ReadAsync(buffer.AsMemory(), pumpCts.Token)
                                             .ConfigureAwait(false);
                    if (read <= 0)
                        break;

                    await ffmpegIn.WriteAsync(buffer.AsMemory(0, read), pumpCts.Token)
                                  .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // normal during cancellation
            }
            catch
            {
                // swallow; we'll see errors via ffmpeg exit / stderr if needed
            }
            finally
            {
                try { ffmpegIn.Flush(); } catch { }
                try { ffmpegIn.Close(); } catch { }
                try { ytdlpOut.Close(); } catch { }
            }
        }, pumpCts.Token);

        // Link external cancellation to both processes and the pump
        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                try { pumpCts.Cancel(); } catch { }
                try { if (!ytdlp.HasExited) ytdlp.Kill(entireProcessTree: true); } catch { }
                try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); } catch { }
            });
        }

        IAudioTrackReader reader = new YtDlpTrackReader(_logger, ytdlp, ffmpeg, pumpTask, pumpCts);
        return Task.FromResult(reader);
    }
    
    private sealed class YtDlpTrackReader : IAudioTrackReader
    {
        private readonly ILogger<YtDlpAudioSource> _logger;
        private readonly Process _ytdlp;
        private readonly Process _ffmpeg;
        private readonly Task _pumpTask;
        private readonly CancellationTokenSource _pumpCts;
        private readonly Stream _ffmpegOut;
        private bool _disposed;

        public YtDlpTrackReader(
            ILogger<YtDlpAudioSource> logger,
            Process ytdlp,
            Process ffmpeg,
            Task pumpTask,
            CancellationTokenSource pumpCts)
        {
            _logger = logger;
            _ytdlp = ytdlp;
            _ffmpeg = ffmpeg;
            _pumpTask = pumpTask;
            _pumpCts = pumpCts;
            _ffmpegOut = ffmpeg.StandardOutput.BaseStream;

            _ffmpeg.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    logger.LogError($"ffmpeg: {e.Data}");
                }
            };
            _ffmpeg.BeginErrorReadLine();

            _ytdlp.ErrorDataReceived += (_, e) =>
            {
                var line = e.Data;
                if (string.IsNullOrEmpty(line)) return;

                if (line.StartsWith("[download]") || line.StartsWith("[youtube]"))
                {
                    // Normal noise → Debug (or Trace)
                    logger.LogDebug("yt-dlp: {Line}", line);
                }
                else if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("yt-dlp: {Line}", line);
                }
                else if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("yt-dlp: {Line}", line);
                }
                else
                {
                    // Everything else – adjust to taste
                    logger.LogInformation("yt-dlp: {Line}", line);
                }
            };
            _ytdlp.BeginErrorReadLine();

        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_disposed) return 0;
            if (buffer.Length == 0) return 0;

            var read = await _ffmpegOut.ReadAsync(buffer, ct).ConfigureAwait(false);

            if (read == 0)
            {
                // EOF from ffmpeg; check if it exited cleanly
                if (_ffmpeg.HasExited && _ffmpeg.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"ffmpeg exited with code {_ffmpeg.ExitCode} for this track.");
                }

                if (_ytdlp.HasExited && _ytdlp.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"yt-dlp exited with code {_ytdlp.ExitCode} for this track.");
                }
            }

            return read;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _pumpCts.CancelAsync();

            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not await pump task.");
            }
            finally
            {
                _pumpCts.Dispose();
            }

            try
            {
                await _ffmpegOut.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose ffmpeg output stream.");
            }

            try
            {
                if (!_ffmpeg.HasExited)
                    _ffmpeg.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill ffmpeg process.");
            }
            finally
            {
                _ffmpeg.Dispose();
            }

            try
            {
                if (!_ytdlp.HasExited)
                    _ytdlp.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill yt-dlp process.");
            }
            finally
            {
                _ytdlp.Dispose();
            }
        }
    }
}
