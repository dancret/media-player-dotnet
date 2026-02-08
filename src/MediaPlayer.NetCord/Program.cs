using MediaPlayer;
using MediaPlayer.Input;
using MediaPlayer.NetCord;
using MediaPlayer.NetCord.Misc;
using MediaPlayer.NetCord.Player;
using MediaPlayer.Tracks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using NetCord.Gateway;
using NetCord.Gateway.ReconnectStrategies;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;

var builder = Host.CreateApplicationBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

builder.Logging.ClearProviders();

builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

builder.Logging.AddSimpleConsole(options =>
{
    options.ColorBehavior = LoggerColorBehavior.Enabled;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    options.SingleLine = false;
});
if (builder.Environment.IsProduction())
{
    builder.Logging.AddLog4Net("log4net.config");
}

var services = builder.Services;

var configurationSection = builder.Configuration;

services.AddOptions<FfmpegOptions>()
    .Bind(configurationSection.GetSection(nameof(FfmpegOptions)))
    .Validate(o =>
            !string.IsNullOrWhiteSpace(o.FfmpegPath),
        $"{nameof(FfmpegOptions)}:{nameof(FfmpegOptions.FfmpegPath)} cannot be empty.")
    .ValidateOnStart();

services.AddOptions<YtDlpOptions>()
    .Bind(configurationSection.GetSection(nameof(YtDlpOptions)))
    .Validate(o =>
        !string.IsNullOrWhiteSpace(o.YtDlpPath),
        $"{nameof(YtDlpOptions)}:{nameof(YtDlpOptions.YtDlpPath)} cannot be empty.")
    .ValidateOnStart();

services.AddOptions<TrackResolverOptions>()
    .Bind(configurationSection.GetSection(nameof(TrackResolverOptions)));

services.AddWindowsService();
services.AddSystemd();

CacheConfig.Configure(services, configurationSection);

services
    .AddNetworkGate()
    .AddSingleton<ITrackRequestCache, EasyCachingCache>()
    .AddSingleton<ITrackResolver, YouTubeTrackResolver>()
    .AddSingleton<IAudioSource, YtDlpAudioSource>()
    .AddSingleton<NetCordDiscordPlayerProvider>()
    .AddApplicationCommands()
    .AddDiscordGateway(options =>
    {
        options.ReconnectStrategy = new ReconnectStrategy();
        options.Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates;
    })
    .AddHostedService<DiscordBotService>();

var host = builder
    .Build()
    .AddModules(typeof(Program).Assembly)
    .UseGatewayEventHandlers();

await host.WaitForNetworkAsync(CancellationToken.None);

await host.RunAsync();
