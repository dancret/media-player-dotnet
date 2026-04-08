FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore src/MediaPlayer.NetCord/MediaPlayer.NetCord.csproj
RUN dotnet publish src/MediaPlayer.NetCord/MediaPlayer.NetCord.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore


FROM debian:bookworm AS libdave-build
ARG LIBDAVE_REF=main
WORKDIR /src

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        git \
        build-essential \
        cmake \
        ninja-build \
        pkg-config \
        python3 \
        curl \
        zip \
        unzip \
        tar \
        perl \
    && rm -rf /var/lib/apt/lists/*

RUN git clone --recursive --branch ${LIBDAVE_REF} https://github.com/discord/libdave.git /src/libdave
WORKDIR /src/libdave/cpp

RUN git submodule update --init --recursive \
    && ./vcpkg/bootstrap-vcpkg.sh \
    && make cclean \
    && make shared \
    && find /src/libdave -type f \( -name "libdave.so" -o -name "libdave.so.*" \) -print

RUN mkdir -p /out \
    && cp "$(find /src/libdave -type f -name 'libdave.so' | head -n 1)" /out/libdave.so


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
        libssl3 \
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

COPY --from=libdave-build /out/libdave.so /usr/local/lib/libdave.so

RUN ldconfig

COPY --from=build /app/publish ./
COPY src/MediaPlayer.NetCord/appsettings.json /app/appsettings.json

ENV PATH="/home/app/.local/bin:${PATH}"
ENV LD_LIBRARY_PATH="/usr/local/lib:${LD_LIBRARY_PATH}"

USER app

ENTRYPOINT ["dotnet", "MediaPlayer.NetCord.dll"]
