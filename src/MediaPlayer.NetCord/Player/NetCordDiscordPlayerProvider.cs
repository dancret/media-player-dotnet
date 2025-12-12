using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using System.Collections.Concurrent;

namespace MediaPlayer.NetCord.Player;

public sealed class NetCordDiscordPlayerProvider : IAsyncDisposable
{
    /// <summary>
    /// We will store all instances of players in a single instance of this provider, in memory.
    /// Players will be stored as a pair of voice channels and players, since only one voice channel use is allowed per guild, per bot.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, NetCordDiscordPlayer> _players = new();

    private readonly GatewayClient _gatewayClient;
    private readonly ILogger<NetCordDiscordPlayerProvider> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public NetCordDiscordPlayerProvider(
        ILogger<NetCordDiscordPlayerProvider> logger,
        ILoggerFactory loggerFactory,
        GatewayClient gatewayClient)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _gatewayClient = gatewayClient;

        _gatewayClient.VoiceStateUpdate += GatewayClientOnVoiceStateUpdate;
    }

    public async Task<NetCordDiscordPlayer> GetPlayerAsync(Guild guild, VoiceState voiceState)
    {
        _logger.LogInformation(
            "GetPlayerAsync: guild={GuildId}, userChannel={ChannelId}",
            guild.Id,
            voiceState.ChannelId);

        var channelId = voiceState.ChannelId!.Value;

        // Remove disposed player if found, and continue with creating a new one,
        // else just return existing one.
        if (_players.TryGetValue(channelId, out var existingPlayer))
        {
            if (existingPlayer.IsDisposed)
            {
                _logger.LogInformation(
                    "GetPlayerAsync: removing disposed player for channel {ChannelId}",
                    channelId);
                _players.TryRemove(channelId, out _);
            }
            else
            {
                if (guild.VoiceStates.TryGetValue(_gatewayClient.Id, out var botVoiceState) &&
                    botVoiceState.ChannelId == channelId)
                {
                    // Bot is already in the right channel and player is healthy -> reuse
                    _logger.LogInformation(
                        "GetPlayerAsync: reusing existing player for channel {ChannelId}",
                        channelId);
                    return existingPlayer;
                }

                _logger.LogWarning(
                    "GetPlayerAsync: existing player found for channel {ChannelId}, but bot connected to {BotChannelId} or player invalid — recreating",
                    channelId,
                    botVoiceState?.ChannelId);
                // Wrong channel, clean up then create again
                _players.TryRemove(channelId, out _);

                try
                {
                    await existingPlayer.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose stale NetCordDiscordPlayer for channel {ChannelId}", channelId);
                }
            }
        }

        VoiceClient? voiceClient;

        try
        {
            _logger.LogInformation(
                "Voice join attempt: guild={GuildId}, channel={ChannelId}",
                guild.Id,
                channelId);

            voiceClient = await _gatewayClient.JoinVoiceChannelAsync(
                guild.Id,
                channelId);

            _logger.LogInformation(
                "Voice join request accepted: guild={GuildId}, channel={ChannelId}",
                guild.Id,
                channelId);

            await voiceClient.StartAsync();

            _logger.LogInformation(
                "Voice client started: guild={GuildId}, channel={ChannelId}",
                guild.Id,
                channelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Voice join failed: guild={GuildId}, channel={ChannelId}",
                guild.Id,
                channelId);

            throw;
        }

        var logger = _loggerFactory.CreateLogger<NetCordDiscordPlayer>();
        var player = new NetCordDiscordPlayer(channelId, voiceClient, logger, _loggerFactory);

        await player.InitializeAsync();
        _players[channelId] = player;

        if (guild.VoiceStates.TryGetValue(_gatewayClient.Id, out var botState))
        {
            _logger.LogDebug(
                "Bot voice state after join: channel={ChannelId}",
                botState.ChannelId);
        }
        else
        {
            _logger.LogDebug("Bot voice state after join: not present in cache");
        }

        return player;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var netCordDiscordPlayer in _players)
        {
            try
            {
                await netCordDiscordPlayer.Value.DisposeAsync();
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to dispose players.");
            }
        }

        _gatewayClient.VoiceStateUpdate -= GatewayClientOnVoiceStateUpdate;
        _gatewayClient.Dispose();
        _loggerFactory.Dispose();
    }

    /// <summary>
    /// Auto-leaves when the last non-bot user leaves the bot's voice channel.
    /// </summary>
    private async ValueTask GatewayClientOnVoiceStateUpdate(VoiceState state)
    {
        try
        {
            // Get the guild from cache
            _gatewayClient.Cache.Guilds.TryGetValue(state.GuildId, out var guild);
            if (guild is null)
                return;

            // If the bot has no voice state in this guild, we’re not connected -> nothing to do
            if (!guild.VoiceStates.TryGetValue(_gatewayClient.Id, out var botVoiceState))
                return;

            // Bot isn't actually in a channel (shouldn't really happen, but be safe)
            var botChannelId = botVoiceState.ChannelId;
            if (botChannelId is null)
                return;

            // Ignore events about the bot's own voice state – we care about other users leaving
            if (state.UserId == _gatewayClient.Id)
                return;

            // Recalculate how many users are still in the same channel as the bot
            var usersInBotChannel = guild.VoiceStates.Values.Count(vs => vs.ChannelId == botChannelId);

            // At this point, guild.VoiceStates may still think the user is in botChannel.
            // If the *new* state says they LEFT or MOVED AWAY from botChannel,
            // manually subtract them from the count.
            if (guild.VoiceStates.TryGetValue(state.UserId, out var cachedUserVoiceState))
            {
                var cachedWasInBotChannel = cachedUserVoiceState.ChannelId == botChannelId;
                var nowInBotChannel = state.ChannelId == botChannelId;

                if (cachedWasInBotChannel && !nowInBotChannel)
                {
                    usersInBotChannel--;
                }
            }

            // If there is at least one non-bot user left, do nothing
            if (usersInBotChannel > 1)
                return;

            // Find the player that corresponds to this voice channel
            var player = _players.Values.SingleOrDefault(p => p.VoiceChannelId == botChannelId.Value);
            if (player is null)
                return;

            _logger.LogInformation(
                "No more listeners in guild {GuildId} / channel {ChannelId}, stopping and disposing player.",
                state.GuildId,
                botChannelId);

            // Stop playback and dispose player
            await player.DisposeAsync();

            // Update to latest voice channel status to show as left
            await _gatewayClient.UpdateVoiceStateAsync(new VoiceStateProperties(state.GuildId, state.ChannelId));

            foreach (var (key, value) in _players.ToArray())
            {
                if (!ReferenceEquals(value, player)) continue;

                _players.TryRemove(key, out _);
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error in GatewayClientOnVoiceStateUpdate (NetCord VoiceStateUpdate handler).");
        }
    }
}
