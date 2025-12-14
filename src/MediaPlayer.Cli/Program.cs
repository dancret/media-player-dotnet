using MediaPlayer;
using MediaPlayer.Cli;
using MediaPlayer.Input;
using MediaPlayer.Output;
using MediaPlayer.Playback;
using MediaPlayer.Tracks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Information)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
});

var appLogger = loggerFactory.CreateLogger("MediaPlayer.Cli");
var cliPlayerLogger = loggerFactory.CreateLogger<CliPlayer>();
var ytLogger = loggerFactory.CreateLogger<YtDlpAudioSource>();
var localFileLogger = loggerFactory.CreateLogger<LocalFileAudioSource>();

var localSource = new LocalFileAudioSource(localFileLogger, t => t.Uri);
var ytSource = new YtDlpAudioSource(ytLogger, new OptionsWrapper<YtDlpAudioSourceOptions>(new YtDlpAudioSourceOptions()), t => t.Uri );

var routingSource = new RoutingAudioSource(
    inputSelector: track => track.Input,
    sources: new Dictionary<TrackInput, IAudioSource>
    {
        [TrackInput.LocalFile] = localSource,
        [TrackInput.YouTube] = ytSource
    }
    // no fallback for now
);

// Create source/sink; keep simple, you can refine later.
IAudioSource source = routingSource;
await using IAudioSink sink = new FfplaySink();

// Create resolvers
var ytTrackResolverLogger = loggerFactory.CreateLogger<YouTubeTrackResolver>();
var ytTrackResolver = new YouTubeTrackResolver(ytTrackResolverLogger, new OptionsWrapper<YouTubeTrackResolverOptions>(new YouTubeTrackResolverOptions()));
var localTrackResolverLogger = loggerFactory.CreateLogger<LocalFileTrackResolver>();
var localTrackResolver = new LocalFileTrackResolver(localTrackResolverLogger);
var routingTrackResolverLogger = loggerFactory.CreateLogger<RoutingTrackResolver>();
var trackResolver = new RoutingTrackResolver([ytTrackResolver, localTrackResolver], routingTrackResolverLogger);

// Create CLI player
await using var player = new CliPlayer(trackResolver, source, sink, cliPlayerLogger);

// Cancellation for whole app
using var cts = new CancellationTokenSource();

// Handle Ctrl+C
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Ctrl+C pressed, shutting down...");
    cts.Cancel();
};

// Start player (this internally starts the PlaybackLoop)
await player.StartAsync(cts.Token);

// Show initial help
PrintHelp();

try
{
    // Simple REPL
    while (!cts.IsCancellationRequested)
    {
        Console.Write("> ");
        var line = Console.ReadLine();

        if (line is null)
        {
            // stdin closed
            break;
        }

        if (string.IsNullOrWhiteSpace(line))
            continue;

        var parts = ParseCommandLine(line);
        if (parts.Length == 0)
            continue;

        var cmd = parts[0].ToLowerInvariant();
        var args1 = parts.Skip(1).ToArray();

        try
        {
            switch (cmd)
            {
                case "help":
                    PrintHelp();
                    break;

                case "enqueue":
                    await HandleEnqueueAsync(player, args1);
                    break;

                case "playnow":
                    await HandlePlayNowAsync(player, args1);
                    break;

                case "pause":
                    await player.PauseAsync();
                    break;

                case "resume":
                    await player.ResumeAsync();
                    break;

                case "skip":
                    await player.SkipAsync();
                    break;

                case "clear":
                    await player.ClearAsync();
                    break;

                case "stop":
                    await player.StopAsync();
                    Console.WriteLine("Playback stopped. You can still enqueue/play again.");
                    break;

                case "status":
                    player.PrintStatus();
                    break;

                case "repeat":
                    HandleRepeat(player, args1);
                    break;

                case "shuffle":
                    HandleShuffle(player, args1);
                    break;

                case "quit":
                case "exit":
                    Console.WriteLine("Exiting...");
                    cts.Cancel();
                    break;

                default:
                    Console.WriteLine("Unknown command. Type 'help' for a list of commands.");
                    break;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Graceful shutdown
            break;
        }
        catch (Exception ex)
        {
            appLogger.LogError(ex, "Error while executing command '{Command}'", cmd);
        }
    }
}
finally
{
    try
    {
        // Try to stop gracefully
        await player.StopAsync();
    }
    catch { /* ignore during shutdown */ }
}

// ---- Local helpers ----

static void PrintHelp()
{
    Console.WriteLine("Commands:");
    Console.WriteLine("  enqueue <file1> [file2 ...]   - Enqueue one or more local files");
    Console.WriteLine("  playnow <file>                - Immediately play file, interrupting current track");
    Console.WriteLine("  pause                         - Pause playback");
    Console.WriteLine("  resume                        - Resume playback");
    Console.WriteLine("  skip                          - Skip current track");
    Console.WriteLine("  clear                         - Clear the queue");
    Console.WriteLine("  stop                          - Stop playback (player stays usable)");
    Console.WriteLine("  status                        - Show current state and track info");
    Console.WriteLine("  repeat off|one|all            - Set repeat mode");
    Console.WriteLine("  shuffle on|off|toggle         - Set or toggle shuffle mode");
    Console.WriteLine("  help                          - Show this help");
    Console.WriteLine("  quit | exit                   - Exit the application");
    Console.WriteLine();
    Console.WriteLine("Note: requires ffmpeg and ffplay available on PATH.");
}

static async Task HandleEnqueueAsync(CliPlayer player, string[] args)
{
    if (args.Length == 0)
    {
        Console.WriteLine("Usage: enqueue <file1> [file2 ...]");
        return;
    }

    await player.EnqueueFilesAsync(args);
    Console.WriteLine($"Enqueued {args.Length} file(s).");
}

static async Task HandlePlayNowAsync(CliPlayer player, string[] args)
{
    if (args.Length != 1)
    {
        Console.WriteLine("Usage: playnow <file>");
        return;
    }

    await player.PlayNowFileAsync(args[0]);
    Console.WriteLine($"Playing now: {args[0]}");
}

static void HandleRepeat(CliPlayer player, string[] args)
{
    if (args.Length != 1)
    {
        Console.WriteLine("Usage: repeat off|one|all");
        return;
    }

    try
    {
        player.SetRepeatModeFromString(args[0]);
        Console.WriteLine($"Repeat mode set to {player.RepeatMode}.");
    }
    catch (ArgumentException ex)
    {
        Console.WriteLine(ex.Message);
    }
}

static void HandleShuffle(CliPlayer player, string[] args)
{
    if (args.Length == 0)
    {
        Console.WriteLine($"Shuffle is currently {(player.Shuffle ? "on" : "off")}.");
        Console.WriteLine("Usage: shuffle on|off|toggle");
        return;
    }

    try
    {
        player.SetShuffleFromString(args[0]);
        Console.WriteLine($"Shuffle is now {(player.Shuffle ? "on" : "off")}.");
    }
    catch (ArgumentException ex)
    {
        Console.WriteLine(ex.Message);
    }
}

static string[] ParseCommandLine(string line)
{
    var result = new List<string>();
    if (string.IsNullOrWhiteSpace(line))
        return Array.Empty<string>();

    var current = new System.Text.StringBuilder();
    bool inQuotes = false;

    for (int i = 0; i < line.Length; i++)
    {
        var c = line[i];

        if (c == '"')
        {
            inQuotes = !inQuotes;
            continue; // don't include the quote itself
        }

        if (char.IsWhiteSpace(c) && !inQuotes)
        {
            if (current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }
        else
        {
            current.Append(c);
        }
    }

    if (current.Length > 0)
    {
        result.Add(current.ToString());
    }

    return result.ToArray();
}
