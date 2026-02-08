FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore ./src/MediaPlayer.NetCord/MediaPlayer.NetCord.csproj
RUN dotnet publish ./src/MediaPlayer.NetCord/MediaPlayer.NetCord.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        ffmpeg \
        nodejs \
        libsodium23 \
        libopus0 \
    && rm -rf /var/lib/apt/lists/*

RUN getent group app || groupadd -r app \
    && id -u app >/dev/null 2>&1 || useradd -r -g app -d /home/app -s /usr/sbin/nologin app \
    && mkdir -p /app/Logs /home/app/.local/bin \
    && chown -R app:app /app /home/app

RUN ln -sf /usr/lib/x86_64-linux-gnu/libopus.so.0 /usr/lib/x86_64-linux-gnu/libopus.so

RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /home/app/.local/bin/yt-dlp \
    && chmod a+rx /home/app/.local/bin/yt-dlp

RUN printf '%s\n' \
            '--js-runtimes node:/usr/bin/node' \
            '--no-warnings' \
            > /etc/yt-dlp.conf

RUN apt-get purge -y --auto-remove

COPY --from=build /app/publish ./
COPY src/MediaPlayer.NetCord/appsettings.json /app/appsettings.json

ENV PATH="/home/app/.local/bin:${PATH}"

USER app

ENTRYPOINT ["dotnet", "MediaPlayer.NetCord.dll"]
