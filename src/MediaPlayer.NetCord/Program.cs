using MediaPlayer.Ffmpeg;
using MediaPlayer.Input;
using MediaPlayer.NetCord;
using MediaPlayer.NetCord.Misc;
using MediaPlayer.NetCord.Player;
using MediaPlayer.Tracks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;

var builder = Host.CreateApplicationBuilder(args);


builder.Logging.ClearProviders();

builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

builder.Logging.AddSimpleConsole(options =>
{
    options.ColorBehavior = LoggerColorBehavior.Enabled;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    options.SingleLine = false;
});
#if !DEBUG
builder.Logging.AddLog4Net("log4net.config");
#endif

var services = builder.Services;

var configurationSection = builder.Configuration;

services.AddOptions<YtDlpAudioSourceOptions>()
    .Bind(configurationSection.GetSection(nameof(YtDlpAudioSourceOptions)))
    .Validate(o =>
        !string.IsNullOrWhiteSpace(o.YtDlpPath),
        $"{nameof(YtDlpAudioSourceOptions)}:{nameof(YtDlpAudioSourceOptions.YtDlpPath)} cannot be empty.")
    .Validate(o =>
            !string.IsNullOrWhiteSpace(o.FfmpegPath),
        $"{nameof(YtDlpAudioSourceOptions)}:{nameof(YtDlpAudioSourceOptions.FfmpegPath)} cannot be empty.")
    .ValidateOnStart();

services.AddOptions<YouTubeTrackResolverOptions>()
    .Bind(configurationSection.GetSection(nameof(YouTubeTrackResolverOptions)))
    .Validate(o => 
            !string.IsNullOrWhiteSpace(o.YtDlpPath), 
        $"{nameof(YouTubeTrackResolverOptions)}:{nameof(YouTubeTrackResolverOptions.YtDlpPath)} cannot be empty.")
    .ValidateOnStart();

services.AddWindowsService();
services.AddSystemd();

services.AddEasyCaching(options =>
{
    options.UseInMemory("default");
});

services
    .ConfigureHttpClientDefaults(b => b.RemoveAllLoggers())
    .AddSingleton<ITrackRequestCache, EasyCachingCache>()
    .AddSingleton<ITrackResolver, YouTubeTrackResolver>()
    .AddSingleton<IAudioSource, YtDlpAudioSource>()
    .AddSingleton<NetCordDiscordPlayerProvider>()
    .AddApplicationCommands()
    .AddDiscordGateway(options =>
    {
        options.Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates;
    })
    .AddHostedService<DiscordBotService>();

var host = builder
    .Build()
    .AddModules(typeof(Program).Assembly)
    .UseGatewayEventHandlers();

await host.RunAsync();
