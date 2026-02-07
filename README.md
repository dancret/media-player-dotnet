# MediaPlayer.NET

A modular, extensible audio playback engine written in C# and targeting modern .NET (currently .NET 10).
The goal of this project is to provide a clean, predictable playback model, built around clear abstractions for audio sources, sinks, and players. The design favors transparency, debuggability, and well-defined concurrency boundaries, without being tied to any specific UI or hosting environment.

This repository contains the core playback engine, together with example integrations (CLI and Discord/NetCord) that demonstrate how the abstractions can be composed into real applications.

---

## What this project is

At its core, this is a media playback framework rather than a monolithic bot or app.  
It provides:

- A PlaybackSession and PlaybackLoop that handle streaming, buffering, pacing, pausing, and cancellation.
- Pluggable IAudioSource implementations (local files, YouTube via yt-dlp + FFmpeg, future providers).
- Pluggable IAudioSink implementations (Discord Opus sink, CLI sink, file writer sinks, etc.).
- A PlayerBase class offering queue management, command dispatching, state tracking, shuffle/repeat behavior, and unified error handling.
- Example concrete players:
  - CLI player — minimal interactive command-line controller.
  - Discord player (NetCord) — demonstrates in-voice playback, pacing, and concurrency integration.

The architecture is deliberately modular so that new integrations (e.g., MAUI app, web API, radio streaming source) can be added cleanly.

---

## What it can do today

### Playback features
- Stream audio from FFmpeg-backed sources  
- Support YouTube/yt-dlp inputs, including resolving URLs and playlists  
- Queue system: enqueue, play now, skip, stop  
- Shuffle, repeat, and queue inspection  
- Pause/resume with proper pacing  
- Error handling with reasonable retry behavior  

### Integration examples
- CLI interface with simple commands  
- Discord voice playback using NetCord  
- Structured logging (Console, Debug, file) with user-controlled verbosity  

### Concurrency model
- Commands sent via channels  
- PlayerLoop is single-consumer  
- Session and sources use async streaming and cancellation tokens  
- Clear separation between public API and playback engine internals

---

## How to use it

### As a library
Reference MediaPlayer.Core in your application and:

1. Implement or reuse an IAudioSource.
2. Implement or reuse an IAudioSink.
3. Derive your own player from PlayerBase.
4. Wire your commands (REST, GUI, bot commands) into the player’s command channel.

### As a CLI app
Minimal example:

```bash
playnow "path/to/song.mp3"
enqueue https://www.youtube.com/watch?v=
pause
resume
skip
stop
queue
```

### As a Discord bot
The Discord integration project demonstrates:

- Voice connection lifecycle  
- Creating an audio sink that writes Opus frames  
- Forwarding Discord commands into the player’s queue  

This integration is intentionally lightweight and meant to be expanded as needed.

---

## How to extend it

The framework is intentionally built for extension without modifying the core.

### Adding a new audio source
Implement `IAudioSource`:

- HTTP streams  
- Local folders or playlists  
- Radio/Icecast  
- Spotify or other APIs  
- TTS or generated audio  

### Adding a new audio sink
Implement `IAudioSink`:

- Discord (other libraries)  
- WASAPI  
- File writer  
- Network streaming  
- Custom hardware  

### Extending player behavior
Override methods in your player derived from PlayerBase:

- OnBeforeEnqueueAsync  
- OnSessionEnded  
- OnStateTransition  
- OnTrackFailedAsync  

Use these to implement permissions, crossfade, custom policies, etc.

---

## Repository structure (overview)

The repository is organized around a strict separation between the core playback engine
and the integration layers that host it:

### MediaPlayer.Core
The heart of the system. All reusable logic and abstractions live here:
- Playback loop, session control, and pacing logic
- Audio sources (yt-dlp + FFmpeg, local file sources, etc.)
- Audio sinks (Discord Opus sink, CLI sink, test sinks)
- Track model, resolvers, metadata structures
- Shared utilities and internal helpers

This project should remain UI-agnostic and platform-agnostic.

### MediaPlayer.Cli
A minimal, self-contained command-line wrapper around the core player:
- Hosts a PlayerBase instance
- Provides a simple REPL-like interface with commands (playnow, enqueue, pause, skip, etc.)
- Useful for debugging, validating the playback loop, and testing sources/sinks without Discord

### MediaPlayer.Discord
An example integration based on NetCord:
- DiscordPlayer (derived from PlayerBase)
- DiscordAudioSink (writes Opus frames to the voice connection)
- Basic application commands for interacting with the player
- Voice connection lifecycle management

This project demonstrates how to embed the playback engine into a real external environment.

The structure is intentionally modular:  
**Core is the framework; CLI and Discord integrations are thin host layers that exercise and validate it.**

---

## Requirements

- .NET 10 SDK  
- FFmpeg + FFplay installed and on PATH  
- yt-dlp installed  
- Windows, Linux, or macOS  
- Discord integration requires a bot token and voice permissions  

---

## Disclaimer

Parts of this codebase were generated or refactored with the assistance of AI tooling (ChatGPT / LLMs).  
However, the project’s architecture, abstractions, and design direction were primarily authored and guided by me.  
AI acted as a shovel: a tool for speeding up the groundwork, not the architect determining what gets built or how.

---

## License

Licensed under MIT. See `LICENSE.txt`.
