# Build from this directory:
#   docker build -t mezube .
# Run:
#   docker run --rm -e DOTNET_ENVIRONMENT=prod \
#     -v "$PWD/appsettings.prod.local.json:/app/appsettings.prod.local.json:ro" \
#     -v mezube-temp:/app/temp mezube
#
# FFmpeg 8.0.1 from Assets/ffmpeg/ffmpeg_8.0.1.orig.tar.xz
#
# Audio + common video codecs for STN (H.264 / H.265 / VP8 / VP9 / AV1) + WHIP.
# OpenSSL (not gnutls) is required for WHIP DTLS. Native demux/decode stay on
# via default configure; external libs below cover encode + fast AV1 decode.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS ffmpeg-build
WORKDIR /src/ffmpeg
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential pkg-config yasm nasm zlib1g-dev libnuma-dev \
        libssl-dev \
        libopus-dev libmp3lame-dev libvorbis-dev \
        libx264-dev libx265-dev libvpx-dev \
        libaom-dev libdav1d-dev libsvtav1enc-dev \
    && rm -rf /var/lib/apt/lists/*

COPY Assets/ffmpeg/ffmpeg_8.0.1.orig.tar.xz /tmp/ffmpeg_8.0.1.orig.tar.xz
RUN tar -xJf /tmp/ffmpeg_8.0.1.orig.tar.xz -C /tmp \
    && cd /tmp/ffmpeg-8.0.1 \
    && ./configure \
        --prefix=/usr/local \
        --disable-debug \
        --disable-doc \
        --disable-ffplay \
        --disable-ffprobe \
        --disable-shared \
        --enable-static \
        --enable-gpl \
        --enable-version3 \
        --enable-network \
        --enable-openssl \
        --enable-libopus \
        --enable-libmp3lame \
        --enable-libvorbis \
        --enable-libx264 \
        --enable-libx265 \
        --enable-libvpx \
        --enable-libaom \
        --enable-libdav1d \
        --enable-libsvtav1 \
        --enable-muxer=whip \
        --extra-libs="-lpthread -lm" \
    && make -j"$(nproc)" \
    && make install \
    && strip /usr/local/bin/ffmpeg \
    && /usr/local/bin/ffmpeg -hide_banner -muxers 2>/dev/null | grep -E '^\s*E\s+whip\b' \
    && /usr/local/bin/ffmpeg -hide_banner -encoders 2>/dev/null | grep -E 'libopus|libx264|libx265|libvpx|libaom|libsvtav1' \
    && /usr/local/bin/ffmpeg -hide_banner -decoders 2>/dev/null | grep -E 'h264|hevc|vp8|vp9|av1|libdav1d' \
    && /usr/local/bin/ffmpeg -hide_banner -protocols 2>/dev/null | grep -E 'https|dtls|tls' \
    && rm -rf /tmp/ffmpeg-8.0.1 /tmp/ffmpeg_8.0.1.orig.tar.xz

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Mezube.csproj ./
RUN dotnet restore Mezube.csproj

COPY . ./
RUN dotnet publish Mezube.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Runtime .so for the external codecs linked into the static ffmpeg binary.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3 python3-pip ca-certificates curl unzip \
        libssl3t64 libnuma1 \
        libopus0 libmp3lame0 libvorbis0a libvorbisenc2 \
        libx264-164 libx265-199 libvpx9 libaom3 libdav1d7 \
        libsvtav1enc1d1 \
    && pip3 install --break-system-packages --no-cache-dir yt-dlp \
    && curl -fsSL https://deno.land/install.sh | DENO_INSTALL=/usr/local sh \
    && rm -rf /var/lib/apt/lists/*

COPY --from=ffmpeg-build /usr/local/bin/ffmpeg /usr/local/bin/ffmpeg
COPY --from=build /app/publish .

ENV DOTNET_ENVIRONMENT=prod \
    Mezube__YtDlpPath=yt-dlp \
    Mezube__YtDlpJsRuntime=deno \
    Mezube__YtDlpJsRuntimePath=/usr/local/bin/deno \
    Mezube__FfmpegPath=/usr/local/bin/ffmpeg \
    Mezube__TempDir=/app/temp \
    Mezube__PostgresConnectionString=Host=postgres;Port=5432;Database=mezube;Username=mezube;Password=mezube \
    Mezube__RedisConnectionString=redis:6379

RUN mkdir -p /app/temp \
    && /usr/local/bin/ffmpeg -hide_banner -version \
    && /usr/local/bin/ffmpeg -hide_banner -muxers 2>/dev/null | grep -E '^\s*E\s+whip\b' \
    && /usr/local/bin/deno --version

VOLUME ["/app/temp"]
ENTRYPOINT ["dotnet", "Mezube.dll"]
