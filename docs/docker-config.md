# Docker configuration (bind-mount appsettings.json)

To manage configuration per container, create a host-side `appsettings.json` and bind-mount it to `/app/appsettings.json`.

```bash
docker run -d --name mediaplayer-netcord \
  -v /path/to/appsettings.json:/app/appsettings.json:ro \
  -v /path/to/logs:/app/Logs \
  mediaplayer-netcord
```

Each container can point to a different host `appsettings.json`.

Notes:
- `:ro` makes the config mount read-only inside the container.
- If you use `docker-compose.yml` on DSM, use absolute host paths (for example `/volume1/...`) instead of repo-relative paths like `./deploy/...`.

---

# OS-level runtime packages

Installed via `apt-get` in the runtime stage:

| Package           | Purpose                                |
| ----------------- | -------------------------------------- |
| `ca-certificates` | TLS / HTTPS trust store                |
| `curl`            | Downloading external tools (yt-dlp)    |
| `ffmpeg`          | Audio decoding / transcoding           |
| `nodejs`          | JavaScript runtime for yt-dlp          |
| `libsodium23`     | Required by Discord / voice encryption |
| `libopus0`        | Opus audio codec (voice)               |


Additional notes:

* A symbolic link is created for libopus.so to satisfy native library lookup expectations.
* apt-get purge --auto-remove is executed afterward to keep the image size minimal.
