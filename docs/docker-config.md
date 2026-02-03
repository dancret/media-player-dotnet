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
