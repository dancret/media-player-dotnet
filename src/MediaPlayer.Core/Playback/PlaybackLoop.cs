using System.Threading.Channels;
using MediaPlayer.Input;
using MediaPlayer.Output;
using MediaPlayer.Tracks;
using Microsoft.Extensions.Logging;
// ReSharper disable MethodHasAsyncOverload

namespace MediaPlayer.Playback;

/// <summary>
/// Represents a playback loop that manages audio playback by processing commands,
/// handling state changes, and coordinating between an audio source and sink.
/// </summary>
/// <remarks>
/// The <see cref="PlaybackLoop"/> class is responsible for managing the playback lifecycle,
/// including handling commands, managing playback state, and coordinating audio data flow
/// between the provided <see cref="IAudioSource"/> and <see cref="IAudioSink"/>.
/// </remarks>
/// <param name="source">
/// The audio source that provides audio data for playback.
/// </param>
/// <param name="sink">
/// The audio sink that consumes audio data for playback.
/// </param>
/// <param name="logger">
/// The logger instance used for logging playback loop activities.
/// </param>
/// <param name="queueCapacity">
/// The maximum number of commands that can be queued for processing. Defaults to 256.
/// </param>
internal sealed class PlaybackLoop(
    IAudioSource source,
    IAudioSink sink,
    ILogger logger,
    int queueCapacity = 256)
    : IAsyncDisposable
{
    #region Fields

    /// <summary>
    /// Represents an internal channel used for communication within the playback loop,
    /// where playback-related commands are queued and processed asynchronously.
    /// </summary>
    /// <remarks>
    /// The channel is bounded, with a configurable capacity, allowing a single consumer
    /// and multiple producers. It ensures thread-safe communication and supports
    /// cancellation as part of the playback loop's lifecycle.
    /// </remarks>
    private readonly Channel<PlayerCommand> _commands = Channel.CreateBounded<PlayerCommand>(new BoundedChannelOptions(queueCapacity)
    {
        SingleReader = true,
        SingleWriter = false
    });

    /// <summary>
    /// Manages the internal playlist of tracks queued for playback within the playback loop.
    /// </summary>
    /// <remarks>
    /// This queue is responsible for storing and organizing tracks to be processed in a
    /// sequential or prioritized order. It supports operations such as adding tracks,
    /// removing duplicates, clearing the queue, and retrieving a snapshot of its current state.
    /// The queue ensures thread-safe interactions and is a central component of the playback
    /// management system.
    /// </remarks>
    private readonly TrackQueue _queue = new();

    /// <summary>
    /// Represents the internal cancellation mechanism for the playback loop, allowing
    /// the controlled shutdown or interruption of the loop and related operations.
    /// </summary>
    /// <remarks>
    /// This field is used to create and manage cancellation tokens for various tasks
    /// within the playback loop, enabling cooperative cancellation and cleanup of
    /// resources. It is linked with external cancellation tokens when the playback
    /// loop is executed.
    /// </remarks>
    private CancellationTokenSource? _loopCts;

    /// <summary>
    /// Represents a cancellation token source used to manage and control the lifecycle
    /// of an individual playback session within the playback loop.
    /// </summary>
    /// <remarks>
    /// This token source is employed to signal and handle cancellation of the currently
    /// active playback session, ensuring graceful termination of resources and transitions
    /// between playback states. It is reset or disposed as new sessions are initiated
    /// or when the playback loop is stopped.
    /// </remarks>
    private CancellationTokenSource? _sessionCts;

    /// <summary>
    /// Represents the currently active playback session, managing the state of audio playback,
    /// including the track being played and its associated metadata.
    /// </summary>
    /// <remarks>
    /// This variable holds a reference to an instance of <see cref="PlaybackSession"/> if a session
    /// is active, or null if no session is currently in progress. It is accessed to track playback
    /// progress and provide details about the ongoing session.
    /// </remarks>
    private PlaybackSession? _currentSession;

    /// <summary>
    /// Represents the current state of the playback loop, indicating the playback status
    /// such as idle, playing, paused, or stopped.
    /// </summary>
    /// <remarks>
    /// The state is used to track and manage the lifecycle of playback, with transitions
    /// determined by player actions and user commands. It is a critical part of ensuring
    /// consistent behavior in the playback loop.
    /// </remarks>
    private PlayerState _state = PlayerState.Idle;

    #endregion

    #region Properties

    /// <summary>
    /// Defines the repeat behavior for playback, determining how tracks are replayed when the end of the queue or track is reached.
    /// </summary>
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

    /// <summary>
    /// Indicates whether tracks in the playback queue should be played in a random order.
    /// </summary>
    /// <remarks>
    /// When enabled, the playback loop selects the next track randomly from the queue,
    /// rather than in sequential order. The shuffling behavior is applied each time a track
    /// needs to be dequeued for playback.
    /// </remarks>
    public bool Shuffle { get; set; }

    /// <summary>
    /// Provides a read-only snapshot of the current track queue at a specific point in time.
    /// </summary>
    /// <remarks>
    /// The snapshot represents the tracks currently queued for playback, allowing external
    /// components to examine the state of the playback queue without affecting its internal
    /// behavior. The content of the snapshot reflects the queue state at the moment the
    /// property is accessed and does not update dynamically as the queue changes.
    /// </remarks>
    public IReadOnlyList<Track> QueueSnapshot => _queue.Snapshot();

    /// <summary>
    /// Provides information about the currently active playback session, including the track being played,
    /// the current playback state, and the session's start time.
    /// </summary>
    /// <remarks>
    /// Returns null if there is no active playback session. The returned session information is based on the current state
    /// of the playback loop and is updated as the loop progresses through tracks or changes playback state.
    /// </remarks>
    public CurrentSessionInfo? CurrentSession
    {
        get
        {
            var session = _currentSession;
            if (session is null)
                return null;

            return new CurrentSessionInfo(
                session.Track,
                _state,
                session.StartedAt
            );
        }
    }

    #endregion

    #region Events

    /// <summary>
    /// Occurs when the playback state changes, providing the updated <see cref="PlayerState"/>.
    /// </summary>
    /// <remarks>
    /// This event is triggered whenever the playback loop's internal state transitions, such as
    /// starting, pausing, stopping, or completing playback. Subscribers can use this event to
    /// monitor player activity and respond to state changes accordingly.
    /// </remarks>
    public event EventHandler<PlayerState>? OnStateChanged;

    /// <summary>
    /// Occurs whenever the currently playing track changes in the playback loop.
    /// </summary>
    /// <remarks>
    /// This event is triggered when a new track is selected for playback, either
    /// due to user action, the completion of the previous track, or automatic
    /// selection (e.g., shuffle or repeat mode). The event handler receives the
    /// updated track as its event argument, which may be null if no more tracks
    /// are available in the queue.
    /// </remarks>
    public event EventHandler<Track?>? OnTrackChanged;

    /// <summary>
    /// Occurs when a playback session has ended, providing details about the track and the result of the playback.
    /// </summary>
    /// <remarks>
    /// This event is triggered when the playback loop processes the end of a session, whether due to normal completion,
    /// cancellation, or an error. Observers can subscribe to handle session-specific post-processing or updates, such as
    /// updating UI, logging, or queuing the next track for playback. The event provides contextual information via
    /// <see cref="SessionEndedEventArgs"/>, containing the track and the result of the session's termination.
    /// </remarks>
    public event EventHandler<SessionEndedEventArgs>? OnSessionEnded;
    
    #endregion

    #region Public Methods

    /// <summary>
    /// Starts the playback loop asynchronously, processing commands and managing playback state.
    /// </summary>
    /// <param name="externalCt">
    /// A <see cref="CancellationToken"/> that can be used to signal cancellation of the playback loop.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> representing the asynchronous operation of the playback loop.
    /// </returns>
    /// <remarks>
    /// The playback loop listens for commands, processes them, and manages playback state.
    /// It links the provided cancellation token with an internal token to handle cancellation.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the playback loop is canceled via the provided <paramref name="externalCt"/>.
    /// </exception>
    /// <exception cref="Exception">
    /// Thrown when an unexpected error occurs during the playback loop execution.
    /// </exception>
    public async Task RunAsync(CancellationToken externalCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _loopCts = linked;
        logger.LogInformation("Player loop started");

        try
        {
            var reader = _commands.Reader;

            while (await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var cmd))
                {
                    logger.LogInformation("Command received: {Command}", cmd.GetType().Name);
                    await HandleAsync(cmd).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            logger.LogInformation("Player loop canceled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Player loop crashed");
            throw;
        }
    }

    /// <summary>
    /// Asynchronously enqueues a playback command to be processed by the playback loop.
    /// </summary>
    /// <param name="cmd">
    /// The playback command to enqueue. This command determines the action to be performed
    /// by the playback loop, such as playing, pausing, skipping, or stopping playback.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous operation of enqueuing the command.
    /// </returns>
    /// <remarks>
    /// This method adds the specified <paramref name="cmd"/> to the internal command queue.
    /// If the queue is closed or the operation is canceled during shutdown, the exceptions
    /// are handled gracefully. Any other exceptions are logged for diagnostic purposes.
    /// </remarks>
    /// <exception cref="ChannelClosedException">
    /// Thrown if the command queue is closed, which is expected during shutdown.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the operation is canceled due to the playback loop's cancellation token being triggered.
    /// </exception>
    public async Task EnqueueCommandAsync(PlayerCommand cmd)
    {
        try
        {
            var token = _loopCts?.Token ?? CancellationToken.None;
            await _commands.Writer.WriteAsync(cmd, token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // normal during shutdown
        }
        catch (OperationCanceledException) when (_loopCts?.IsCancellationRequested == true)
        {
            // also normal during shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue {Command}", cmd.GetType().Name);
        }
    }

    /// <summary>
    /// Disposes the resources used by the playback loop asynchronously, ensuring that all internal
    /// resources such as cancellation tokens, channels, and sinks are properly released.
    /// </summary>
    /// <returns>
    /// A <see cref="ValueTask"/> representing the asynchronous disposal operation.
    /// </returns>
    /// <remarks>
    /// This method cancels the internal session and playback loop cancellation tokens,
    /// terminates the command channel, and disposes associated resources such as the audio sink.
    /// It ensures graceful cleanup of resources to prevent memory leaks or unintended behavior.
    /// </remarks>
    /// <exception cref="Exception">
    /// Thrown if an error occurs during the disposal process.
    /// </exception>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _commands.Writer.TryComplete();
            _sessionCts?.Cancel();
            _loopCts?.Cancel();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cancel tokens or to complete the channel.");
        }
        finally
        {
            _sessionCts?.Dispose();
            _loopCts?.Dispose();
            await sink.DisposeAsync();
        }
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Handles the execution of a given playback command and updates the player state accordingly.
    /// </summary>
    /// <param name="cmd">
    /// The <see cref="PlayerCommand"/> to process, representing an action such as play, pause, stop, or queue manipulation.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> that represents the asynchronous operation of handling the command.
    /// </returns>
    /// <remarks>
    /// This method processes various command types, such as enqueuing tracks, skipping, pausing, resuming, clearing the queue, or stopping playback.
    /// Commands that alter the playback state (e.g., play, pause, and stop) may trigger session state changes or log specific events.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a command is handled in an invalid player state.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when the provided <paramref name="cmd"/> is null.
    /// </exception>
    private Task HandleAsync(PlayerCommand cmd)
    {
        switch (cmd)
        {
            case EnqueueTracksCmd enq:
                _queue.EnqueueRange(enq.Tracks);
                if (_state is PlayerState.Idle or PlayerState.Stopped)
                {
                    TryStartNext();
                }
                break;

            // This is basically a skip to this track command
            case PlayNowCmd pn:
                logger.LogInformation("PlayNow {TrackId}", pn.Track.Uri);
                _queue.RemoveDuplicatesById(pn.Track.Uri);
                _queue.EnqueueFront(pn.Track);

                if (_currentSession is null || _state is PlayerState.Idle or PlayerState.Stopped)
                {
                    TryStartNext();
                }
                else
                {
                    // Cancel current session; when it ends, SessionEndedCmd will fire,
                    // and THEN we'll pick up the track at the front of the queue.
                    _sessionCts?.Cancel();
                }
                break;

            case SkipCmd:
                // Just cancel; do NOT call TryStartNext here.
                // SessionEndedCmd will arrive and we continue from there.
                _sessionCts?.Cancel();
                break;

            case PauseCmd:
                if (_state == PlayerState.Playing && _currentSession is not null)
                {
                    _currentSession.Pause();
                    SetState(PlayerState.Paused);
                }
                break;

            case ResumeCmd:
                if (_state == PlayerState.Paused && _currentSession is not null)
                {
                    _currentSession.Resume();
                    SetState(PlayerState.Playing);
                }
                break;

            case ClearCmd:
                _queue.Clear();
                break;

            case StopCmd:
                _queue.Clear();
                _sessionCts?.Cancel();
                SetState(PlayerState.Stopped);
                break;

            case SessionEndedCmd sec:
                // Session is over; decide what to do next
                logger.LogInformation("Session ended for {TrackId} with {Reason}: {Details}",
                    sec.Track.Uri, sec.Result.Reason, sec.Result.Details);

                // Notify observers (e.g., Player / Discord layer) first
                OnSessionEnded?.Invoke(
                    this,
                    new SessionEndedEventArgs(sec.Track, sec.Result)
                );

                if (sec.Result.Reason != PlaybackEndReason.Cancelled &&
                    RepeatMode == RepeatMode.All)
                {
                    _queue.EnqueueRange([sec.Track]);
                }

                if (_queue.Count > 0)
                {
                    TryStartNext();
                }
                else
                {
                    SetState(PlayerState.Idle);
                }
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Attempts to start playback of the next track in the queue.
    /// </summary>
    /// <remarks>
    /// This method checks if a current playback session is running. If no session is active, it dequeues
    /// the next track (respecting shuffle settings if enabled) and starts a new playback session.
    /// If the queue is empty, no playback session is started, and the player state is set to Idle.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown if an internal invalid state is encountered during the operation.
    /// </exception>
    private void TryStartNext()
    {
        if (_currentSession is not null) return; // session already running

        var next = _queue.DequeueNext(Shuffle);   // handle shuffle here
        OnTrackChanged?.Invoke(this, next);

        if (next is null)
        {
            SetState(PlayerState.Idle);
            return;
        }

        SetState(PlayerState.Playing);

        _sessionCts?.Dispose();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_loopCts!.Token);

        var session = new PlaybackSession(next, source, sink, logger);
        _currentSession = session;

        // Fire-and-forget: when finished, enqueue SessionEndedCmd
        _ = RunSessionAsync(next, session, _sessionCts.Token);
    }

    /// <summary>
    /// Executes the playback session asynchronously, handling track playback and session lifecycle management.
    /// </summary>
    /// <param name="track">
    /// A <see cref="Track"/> representing the audio track to be played in the session.
    /// </param>
    /// <param name="session">
    /// A <see cref="PlaybackSession"/> object used to manage the playback state and operations.
    /// </param>
    /// <param name="ct">
    /// A <see cref="CancellationToken"/> to monitor for cancellation requests during the session.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> that represents the asynchronous playback session.
    /// </returns>
    /// <remarks>
    /// This method handles playback for a single session by playing the specified track,
    /// managing the session state, and processing cancellation or errors.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the playback session is canceled via the provided <paramref name="ct"/>.
    /// </exception>
    /// <exception cref="Exception">
    /// Thrown when an unexpected error occurs during the playback session execution.
    /// </exception>
    private async Task RunSessionAsync(Track track, PlaybackSession session, CancellationToken ct)
    {
        PlaybackEndResult result;

        try
        {
            result = await session.StartAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new PlaybackEndResult(PlaybackEndReason.Cancelled);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Playback session crashed for track {TrackId}", track.Uri);
            result = new PlaybackEndResult(PlaybackEndReason.Failed, ex.Message);
        }

        // Session is done, clear local references
        _currentSession = null;
        _sessionCts?.Dispose();
        _sessionCts = null;

        // Send the result back into the loop as a command
        try
        {
            await EnqueueCommandAsync(new SessionEndedCmd(track, result));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enqueue stop command.");
        }
    }

    /// <summary>
    /// Updates the current playback state and notifies listeners of the change.
    /// </summary>
    /// <param name="state">
    /// A <see cref="PlayerState"/> value representing the new playback state (e.g., Playing, Paused, Stopped).
    /// </param>
    /// <remarks>
    /// This method changes the internal state of the player and logs the state change.
    /// If the state is updated, it triggers the <c>OnStateChanged</c> event to notify listeners.
    /// Duplicate state updates are ignored.
    /// </remarks>
    private void SetState(PlayerState state)
    {
        if (_state == state) return;
        _state = state;
        logger.LogInformation("State changed: {State}", state);
        OnStateChanged?.Invoke(this, state);
    }

    #endregion
}
