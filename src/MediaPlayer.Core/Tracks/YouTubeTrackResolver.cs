using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MediaPlayer.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaPlayer.Tracks;

/// <summary>
/// Resolves <see cref="Track"/> instances from YouTube URLs or IDs using <c>yt-dlp</c>
/// for metadata discovery.
/// </summary>
/// <remarks>
/// <para>
/// This resolver is responsible for:
/// <list type="bullet">
///   <item><description>Detecting YouTube video and playlist inputs.</description></item>
///   <item><description>Invoking <c>yt-dlp</c> to fetch metadata as JSON.</description></item>
///   <item><description>Optionally caching resolved results via <see cref="ITrackRequestCache"/>.</description></item>
///   <item><description>Emitting one or more <see cref="Track"/> instances with <see cref="TrackInput.YouTube"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// The actual streaming of audio for YouTube tracks is handled by an <see cref="IAudioSource"/>
/// implementation such as <see cref="YtDlpAudioSource"/>; this resolver only produces metadata.
/// </para>
/// </remarks>
public sealed class YouTubeTrackResolver : ITrackResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<YouTubeTrackResolver> _logger;
    private readonly ITrackRequestCache? _cache;
    private readonly SemaphoreSlim _ytDlpSemaphore = new(4, 4); // limit concurrent yt-dlp processes
    private readonly TrackResolverOptions _trackResolverOptions;
    private readonly YtDlpOptions _ytDlpOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeTrackResolver"/> class.
    /// </summary>
    /// <param name="logger">Logger used for diagnostic messages.</param>
    /// <param name="trackResolverOptions">Configuration options for resolving and caching YouTube tracks.</param>
    /// <param name="ytDlpOptions">Configuration options for using yt-dlp.</param>
    /// <param name="cache">
    /// Optional cache used to store resolved tracks keyed by normalized YouTube identifiers.
    /// If <see langword="null" />, no caching is performed.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="logger"/> is <see langword="null" />.
    /// </exception>
    public YouTubeTrackResolver(
        ILogger<YouTubeTrackResolver> logger,
        IOptions<TrackResolverOptions> trackResolverOptions,
        IOptions<YtDlpOptions> ytDlpOptions,
        ITrackRequestCache? cache = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache;
        _trackResolverOptions = trackResolverOptions.Value;
        _ytDlpOptions = ytDlpOptions.Value;
    }

    /// <inheritdoc />
    public string Name => "YouTube";

    /// <inheritdoc />
    public bool CanResolve(TrackRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        return TryParseYouTube(request, out var item) &&
               item.Type is YouTubeItemType.Video or YouTubeItemType.Playlist;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Track> ResolveAsync(TrackRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        return ResolveInternalAsync(request, ct);
    }

    #region Internal async iterator

    /// <summary>
    /// Internal async iterator that performs the actual resolution and caching for YouTube requests.
    /// </summary>
    private async IAsyncEnumerable<Track> ResolveInternalAsync(
        TrackRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!TryParseYouTube(request, out var item) ||
            item.Type is not (YouTubeItemType.Video or YouTubeItemType.Playlist))
        {
            yield break;
        }

        var cacheKey = BuildCacheKey(item);

        // Attempt cache lookup if available.
        if (_cache is not null)
        {
            var cached = await _cache.TryGetAsync(cacheKey, ct).ConfigureAwait(false);

            if (cached is not null)
            {
                _logger.LogDebug(
                    "YouTubeTrackResolver cache hit for key '{CacheKey}'.",
                    cacheKey);

                foreach (var track in cached)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return track;
                }

                yield break;
            }
        }

        IReadOnlyList<Track> resolvedTracks;

        try
        {
            resolvedTracks = item.Type switch
            {
                YouTubeItemType.Video =>
                    await ResolveVideoAsync(item, ct).ConfigureAwait(false),

                YouTubeItemType.Playlist =>
                    await ResolvePlaylistExpandedAsync(item, ct).ConfigureAwait(false),

                _ => []
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "YouTubeTrackResolver failed to resolve item {Type} with id '{Id}'.",
                item.Type, item.Id);
            resolvedTracks = [];
        }

        // Store in cache if available, we have results and cache TTL is not zero.
        if (_cache is not null && resolvedTracks.Count > 0 && _trackResolverOptions.CacheTtl > TimeSpan.Zero)
        {
            try
            {
                await _cache
                    .SetAsync(cacheKey, resolvedTracks, _trackResolverOptions.CacheTtl, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "YouTubeTrackResolver cache store failed for key '{CacheKey}'.",
                    cacheKey);
            }
        }

        foreach (var track in resolvedTracks)
        {
            ct.ThrowIfCancellationRequested();
            yield return track;
        }
    }

    #endregion

    #region Parsing and cache key helpers

    private enum YouTubeItemType
    {
        Video,
        Playlist
    }

    private readonly record struct YouTubeItem(
        YouTubeItemType Type,
        string Id);

    /// <summary>
    /// Attempts to interpret the <see cref="TrackRequest"/> as a YouTube video or playlist.
    /// </summary>
    /// <param name="request">The request to parse.</param>
    /// <param name="item">On success, receives the parsed YouTube item description.</param>
    /// <returns>
    /// <see langword="true" /> if parsing was successful; otherwise, <see langword="false" />.
    /// </returns>
    private static bool TryParseYouTube(TrackRequest request, out YouTubeItem item)
    {
        if (string.IsNullOrWhiteSpace(request.Raw))
        {
            item = default;
            return false;
        }

        // If the caller hints this is YouTube, be more permissive (allow plain IDs).
        if (request.InputHint is TrackInput.YouTube)
        {
            if (TryParseYouTubeUriOrId(request.Raw, out item))
                return true;

            item = default;
            return false;
        }

        // Otherwise, attempt to parse as a URL and inspect the host.
        if (TryParseYouTubeUri(request.Raw, out item))
            return true;

        item = default;
        return false;
    }

    /// <summary>
    /// Attempts to parse a raw string as either a full YouTube URL or a bare ID.
    /// </summary>
    private static bool TryParseYouTubeUriOrId(string raw, out YouTubeItem item)
    {
        if (TryParseYouTubeUri(raw, out item))
            return true;

        // Fallback: treat as a video id if it looks like a typical YouTube video ID.
        if (raw.Length is >= 8 and <= 20 && raw.All(IsVideoIdChar))
        {
            item = new YouTubeItem(YouTubeItemType.Video, raw);
            return true;
        }

        item = default;
        return false;

        static bool IsVideoIdChar(char c) =>
            char.IsLetterOrDigit(c) || c is '_' or '-';
    }

    /// <summary>
    /// Attempts to parse a raw string as a YouTube URL and extract video/playlist ids.
    /// </summary>
    private static bool TryParseYouTubeUri(string raw, out YouTubeItem item)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            item = default;
            return false;
        }

        var host = uri.Host.ToLowerInvariant();

        // Standard YouTube domains.
        if (host.Contains("youtube.com"))
        {
            var query = ParseQuery(uri.Query);

            // Playlist links typically contain "list" parameter.
            // RD playlist are radio mixes, cannot really use them with yt-dlp,
            // so we will pass up the playlist and just try to get the video in the url.
            if (query.TryGetValue("list", out var listId) &&
                !string.IsNullOrWhiteSpace(listId) &&
                !listId.StartsWith("RD"))
            {
                item = new YouTubeItem(YouTubeItemType.Playlist, listId);
                return true;
            }

            // Video links typically contain "v" parameter.
            if (query.TryGetValue("v", out var videoId) && !string.IsNullOrWhiteSpace(videoId))
            {
                item = new YouTubeItem(YouTubeItemType.Video, videoId);
                return true;
            }

            item = default;
            return false;
        }

        // Shortened youtu.be links.
        if (host.Contains("youtu.be"))
        {
            var id = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrWhiteSpace(id))
            {
                item = new YouTubeItem(YouTubeItemType.Video, id);
                return true;
            }
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Builds a stable cache key for a YouTube item and playlist expansion mode.
    /// </summary>
    private static string BuildCacheKey(YouTubeItem item)
    {
        return item.Type switch
        {
            YouTubeItemType.Video => $"yt:video:{item.Id}",
            YouTubeItemType.Playlist => $"yt:playlist:{item.Id}:raw",
            _ => $"yt:unknown:{item.Id}"
        };
    }

    /// <summary>
    /// Parses a URI query string into a dictionary of keys and values.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(query))
            return result;

        // Trim leading '?' if present.
        if (query[0] == '?')
            query = query[1..];

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0 || idx == pair.Length - 1)
                continue;

            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);

            result.TryAdd(key, value);
        }

        return result;
    }

    #endregion

    #region Video / playlist resolution

    /// <summary>
    /// Resolves a single YouTube video by id using <c>yt-dlp</c>.
    /// </summary>
    private async ValueTask<IReadOnlyList<Track>> ResolveVideoAsync(
        YouTubeItem item,
        CancellationToken ct)
    {
        var url = $"https://www.youtube.com/watch?v={item.Id}";

        _logger.LogInformation(
            "YouTubeTrackResolver fetching metadata for video '{VideoId}'.",
            item.Id);

        var args = new[]
        {
            "--dump-single-json",
            "--no-playlist",
            "--no-warnings",
            "--no-download",
            url
        };

        args = AddOptions(args);

        var result = await RunYtDlpAsync(args, ct).ConfigureAwait(false);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
        {
            _logger.LogWarning(
                "YouTubeTrackResolver received non-success exit code {ExitCode} resolving video '{VideoId}'.",
                result.ExitCode, item.Id);
            return Array.Empty<Track>();
        }

        VideoJsonDump? json;

        try
        {
            json = JsonSerializer.Deserialize<VideoJsonDump>(result.Stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "YouTubeTrackResolver failed to deserialize video JSON for '{VideoId}'.",
                item.Id);
            return Array.Empty<Track>();
        }

        if (json is null || string.IsNullOrWhiteSpace(json.title))
        {
            _logger.LogWarning(
                "YouTubeTrackResolver received empty or invalid video metadata for '{VideoId}'.",
                item.Id);
            return Array.Empty<Track>();
        }

        TimeSpan? durationHint = null;
        if (json.duration is > 0)
        {
            durationHint = TimeSpan.FromSeconds(json.duration.Value);
        }

        var track = new Track(
            Uri: url,
            Title: json.title,
            Input: TrackInput.YouTube,
            DurationHint: durationHint);

        return [track];
    }

    /// <summary>
    /// Resolves all items in a YouTube playlist as individual tracks using <c>yt-dlp</c>.
    /// </summary>
    private async ValueTask<IReadOnlyList<Track>> ResolvePlaylistExpandedAsync(
        YouTubeItem item,
        CancellationToken ct)
    {
        var url = $"https://www.youtube.com/playlist?list={item.Id}";

        _logger.LogInformation(
            "YouTubeTrackResolver fetching metadata for playlist '{PlaylistId}'.",
            item.Id);

        var args = new[]
        {
            "--dump-single-json",
            "--flat-playlist",
            "--no-warnings",
            "--no-download",
            url
        };

        args = AddOptions(args);

        var result = await RunYtDlpAsync(args, ct).ConfigureAwait(false);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
        {
            _logger.LogWarning(
                "YouTubeTrackResolver received non-success exit code {ExitCode} resolving playlist '{PlaylistId}'.",
                result.ExitCode, item.Id);
            return Array.Empty<Track>();
        }

        PlaylistJsonDump? json;

        try
        {
            json = JsonSerializer.Deserialize<PlaylistJsonDump>(result.Stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "YouTubeTrackResolver failed to deserialize playlist JSON for '{PlaylistId}'.",
                item.Id);
            return Array.Empty<Track>();
        }

        if (json?.entries is null || json.entries.Length == 0)
        {
            _logger.LogWarning(
                "YouTubeTrackResolver received empty playlist metadata for '{PlaylistId}'.",
                item.Id);
            return Array.Empty<Track>();
        }

        var tracks = new List<Track>(json.entries.Length);

        foreach (var entry in json.entries)
        {
            if (string.IsNullOrWhiteSpace(entry.id))
                continue;

            var videoUrl = $"https://www.youtube.com/watch?v={entry.id}&list={item.Id}";
            var title = string.IsNullOrWhiteSpace(entry.title)
                ? entry.id
                : entry.title;

            TimeSpan? durationHint = null;
            if (entry.duration is > 0)
            {
                durationHint = TimeSpan.FromSeconds(entry.duration.Value);
            }

            tracks.Add(new Track(
                Uri: videoUrl,
                Title: title,
                Input: TrackInput.YouTube,
                DurationHint: durationHint));
        }

        return tracks;
    }

    #endregion

    #region yt-dlp process helper and JSON DTOs

    private sealed class YtDlpResult
    {
        /// <summary>
        /// Gets or sets the standard output content produced by <c>yt-dlp</c>.
        /// </summary>
        public required string Stdout { get; init; }

        /// <summary>
        /// Gets or sets the process exit code.
        /// </summary>
        public required int ExitCode { get; init; }
    }

    /// <summary>
    /// Runs <c>yt-dlp</c> with the specified arguments and captures standard output.
    /// </summary>
    /// <param name="arguments">The arguments to pass to <c>yt-dlp</c>.</param>
    /// <param name="ct">A token used to observe cancellation.</param>
    /// <returns>A <see cref="YtDlpResult"/> containing stdout and exit code.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the process cannot be started.
    /// </exception>
    private async ValueTask<YtDlpResult> RunYtDlpAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        await _ytDlpSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpOptions.YtDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process();
            process.StartInfo = psi;
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Failed to start yt-dlp process using path '{_ytDlpOptions.YtDlpPath}'.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var exitCode = process.ExitCode;

            if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug(
                    "yt-dlp exited with code {ExitCode}. stderr: {Stderr}",
                    exitCode, stderr);
            }

            return new YtDlpResult
            {
                Stdout = stdout,
                ExitCode = exitCode
            };
        }
        finally
        {
            _ytDlpSemaphore.Release();
        }
    }

    private string[] AddOptions(string[] args)
    {
        List<string> cookies = [];
        if (_ytDlpOptions.UseCookies)
        {
            if (!string.IsNullOrWhiteSpace(_ytDlpOptions.CookiesFromBrowser))
            {
                cookies.AddRange(["--cookies-from-browser", _ytDlpOptions.CookiesFromBrowser]);
            }

            if (!string.IsNullOrWhiteSpace(_ytDlpOptions.CookiesFile) && File.Exists(_ytDlpOptions.CookiesFile))
            {
                cookies.AddRange(["--cookies", _ytDlpOptions.CookiesFile]);
            }
        }

        return args.Concat(cookies).ToArray();
    }

    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Match json fields")]
    private sealed record PlaylistJsonDump(string? title, PlaylistEntryJsonDump[]? entries);

    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Match json fields")]
    private sealed record PlaylistEntryJsonDump(string? id, string? title, double? duration);

    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Match json fields")]
    private sealed record VideoJsonDump(string? title, double? duration);

    #endregion
}