# Build from this directory:
#   docker build -t mezube .
# Run:
#   docker run --rm -e DOTNET_ENVIRONMENT=prod \
#     -v "$PWD/appsettings.prod.local.json:/app/appsettings.prod.local.json:ro" \
#     -v mezube-data:/app/data -v mezube-temp:/app/temp mezube
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Mezube.csproj ./
RUN dotnet restore Mezube.csproj

COPY . ./
RUN dotnet publish Mezube.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg python3 python3-pip ca-certificates \
    && pip3 install --break-system-packages --no-cache-dir yt-dlp \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENV DOTNET_ENVIRONMENT=prod \
    Mezube__YtDlpPath=yt-dlp \
    Mezube__FfmpegPath=ffmpeg \
    Mezube__TempDir=/app/temp \
    Mezube__TracksDbPath=/app/data/tracks.db

RUN mkdir -p /app/temp /app/data
VOLUME ["/app/temp", "/app/data"]
ENTRYPOINT ["dotnet", "Mezube.dll"]
