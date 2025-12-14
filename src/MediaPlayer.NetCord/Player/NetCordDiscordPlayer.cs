using MediaPlayer.Input;
using MediaPlayer.Playback;
using Microsoft.Extensions.Logging;
using NetCord.Gateway.Voice;

namespace MediaPlayer.NetCord.Player;

/// <summary>
/// Discord player implementation backed by the MediaPlayer core (PlayerBase + PlaybackLoop).
/// </summary>
/// <remarks>
/// Responsibilities:
/// - Bridge between IDiscordPlayer and MediaPlayer.PlayerBase
/// - Map DiscordPlayerTrack & commands to MediaPlayer.Track operations
/// - Own the Discord voice connection lifetime (via IAudioClient)
/// - Auto-disconnect when no non-bot users remain in the voice channel
/// </remarks>
public sealed class NetCordDiscordPlayer(
    ulong voiceChannelId,
    VoiceClient voiceClient,
    ILogger<NetCordDiscordPlayer> logger,
    ILoggerFactory loggerFactory,
    IAudioSource audioSource)
    : PlayerBase(
        source: audioSource,
        sink: new NetCordAudioSink(voiceClient, loggerFactory.CreateLogger<NetCordAudioSink>()),
        logger: logger)
{    
    private bool _disposed;

    /// <summary>
    /// Indicates that the player is not usable anymore.
    /// </summary>
    public bool IsDisposed => _disposed;

    public ulong VoiceChannelId { get; } = voiceChannelId;

    /// <summary>
    /// Initialize the player: ensure voice connection is valid, subscribe to voice events, and start the playback loop.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NetCordDiscordPlayer));

        try
        {
            logger.LogInformation(
                "Player initialize: channel={ChannelId}, disposed={Disposed}",
                VoiceChannelId,
                _disposed);

            // Enter speaking state to be able to send voice
            await voiceClient.EnterSpeakingStateAsync(SpeakingFlags.Microphone);
            
            logger.LogInformation(
                "NetCordDiscordPlayer: audio client not connected, connecting to voice channel {ChannelId}.",
                VoiceChannelId);

            logger.LogInformation(
                "NetCordDiscordPlayer: starting playback loop for voice channel {ChannelId}.",
                VoiceChannelId);

            await StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NetCordDiscordPlayer.InitializeAsync failed for channel {ChannelId}.", VoiceChannelId);
            throw;
        }
    }

    #region IAsyncDisposable

    public override async ValueTask DisposeAsync()
    {
        logger.LogInformation("NetCordDiscordPlayer: Disposing for voice channel {ChannelId}.", VoiceChannelId);

        try
        {
            // Try to stop playback gracefully
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NetCordDiscordPlayer: error while stopping player during Dispose.");
        }

        try
        {
            await voiceClient.CloseAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NetCordDiscordPlayer: error closing voice client.");
        }

        try
        {
            // Dispose voice client
            voiceClient.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NetCordDiscordPlayer: error disposing voice client.");
        }

        try
        {
            // Dispose of PlayerBase resources (loop, source, sink)
            await base.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NetCordDiscordPlayer: error in PlayerBase.DisposeAsync.");
        }

        _disposed = true;
    }

    #endregion
}
