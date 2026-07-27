# Build from parent folder:
#   docker build -f Mezube/Dockerfile -t mezube .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Mezon.Net/src/Mezon.Net.Generators ./Mezon.Net/src/Mezon.Net.Generators
COPY Mezon.Net/src/Mezon.Net.Core ./Mezon.Net/src/Mezon.Net.Core
COPY Mezon.Net/src/Mezon.Net.Transport ./Mezon.Net/src/Mezon.Net.Transport
COPY Mezon.Net/src/Mezon.Net.Client ./Mezon.Net/src/Mezon.Net.Client
COPY Mezon.Net/src/Mezon.Net.Mmn ./Mezon.Net/src/Mezon.Net.Mmn
COPY Mezon.Net/src/Mezon.Net.Sdk ./Mezon.Net/src/Mezon.Net.Sdk
COPY Mezube ./Mezube

WORKDIR /src/Mezube
RUN dotnet restore Mezube.csproj
RUN dotnet publish Mezube.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg python3 python3-pip ca-certificates \
    && pip3 install --break-system-packages --no-cache-dir yt-dlp \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENV MEZUBE_YTDLP_PATH=yt-dlp \
    MEZUBE_FFMPEG_PATH=ffmpeg \
    MEZUBE_TEMP_DIR=/app/temp \

RUN mkdir -p /app/temp /app/data
ENTRYPOINT ["dotnet", "Mezube.dll"]
