# Mezube

Bot phát nhạc cho [Mezon](https://mezon.ai) — prefix `!`, nguồn YouTube (`yt-dlp`) + direct URL.

- **Voice** (`!play`): STN voice v2 (CDN publish) **or** WHIP (`Mezube:StnWhipEnabled=true` → ffmpeg Opus push to LiveKit)
- **Streaming** (`!stream`): STN WebSocket `/ws` `connect_publisher` / `stop_publisher`
- Legacy STN `/api/playmedia|/api/stopmedia` (URL_INPUT) is **not** used by Mezube anymore; kept on STN for other clients.

Bot chuẩn bị CDN **Ogg Opus 48 kHz**; STN không transcode (xem [mezon-media-station README](../mezon-media-station/README.md)).

## Yêu cầu

- .NET 10 SDK
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) trên PATH
- [ffmpeg](https://ffmpeg.org/) trên PATH (convert → ogg)
- Sibling repo: `F:\projects\mezon\Mezon.Net` (ProjectReference Sdk)

## Cấu hình

Chọn môi trường bằng `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` = `dev` | `prod`.

| File | Vai trò |
|------|---------|
| `appsettings.json` | Shared defaults (prefix, paths, viz/CDN URLs, logging) — **không** chứa token |
| `appsettings.dev.json` / `appsettings.prod.json` | Host / STN / ServerKey theo môi trường |
| `appsettings.dev.local.json` / `appsettings.prod.local.json` (gitignore) | Secrets máy local — `Mezon:BotId` / `Mezon:Token` |

```powershell
# secrets local (không commit)
@"
{
  `"Mezon`": {
    `"BotId`": 123456789,
    `"Token`": `"your_bot_secret`"
  }
}
"@ | Set-Content -Encoding utf8 appsettings.dev.local.json
```

```powershell
$env:DOTNET_ENVIRONMENT='dev'
dotnet run --project Mezube.csproj
```

| Key | Mô tả |
|-----|--------|
| `Mezon:BotId` / `Mezon:Token` | Bot credentials |
| `Mezon:ServerKey` | Gateway Basic-Auth — Dev `defaultkey`, Prod `HTTP3m3zonPr0dkey` |
| `Mezon:Host` / `Mezon:Port` | Dev `dev-mezon.nccsoft.vn:8088`, Prod `gw.mezon.ai:443` |
| `Mezube:StnBaseUrl` | STN origin (vd. `https://stn.mezon.ai`) — derive `/api/v2/voice/*` + `/ws` |
| `Mezube:StnWhipEnabled` | Voice via WHIP (ffmpeg → LiveKit). Default `false` (v2 CDN). Needs ffmpeg `whip` muxer; otherwise falls back to v2 |
| `Mezube:PreparedAudioBitrateKbps` / `PreparedAudioChannels` / `PreparedAudioSampleRate` | Preset cho file CDN “master” trước khi publish |
| `Mezube:WhipAudioBitrateKbps` / `WhipAudioChannels` / `WhipAudioSampleRate` | Preset audio đầu ra WHIP |
| `Mezube:WhipOpusApplication` / `WhipOpusVbr` / `WhipOpusComplexity` | Tune encoder libopus cho quality vs stability |
| `Mezube:WhipPacketLossPercent` / `WhipEnableInbandFec` | Preset resilience khi mạng không hoàn hảo |
| `Mezube:WhipHandshakeTimeoutMs` | Timeout chờ WHIP handshake/publish ready |
| `Mezube:CdnBaseUrl` | Public CDN sau upload |
| `Mezube:BotAvatarUrl` | Avatar bot — embed author + thumbnail fallback |
| `Mezube:VizImageUrl` / `Mezube:VizPositionUrl` | Equalizer sprite + JSON cho `!np` |
| `Mezube:TracksDbPath` | SQLite track library (mặc định `data/tracks.db`) |

### Preset gợi ý

- `quality-first`: `PreparedAudioBitrateKbps=128`, `WhipAudioBitrateKbps=128`, `WhipOpusVbr=on`
- `stable-first`: `WhipAudioBitrateKbps=128`, `WhipEnableInbandFec=true`, `WhipPacketLossPercent=3..5`, cân nhắc `WhipOpusVbr=constrained`
- `160k` là tùy chọn nếu muốn dày hơn, nhưng CPU/bandwidth tăng và khác biệt có thể không đáng kể trong voice room

WHIP hiện vẫn là `ffmpeg -> LiveKit`, nên khi shutdown bot Mezube sẽ cleanup toàn bộ publisher còn sống để không để sót nhạc đang phát.

## Chạy

```powershell
$env:DOTNET_ENVIRONMENT='dev'
dotnet run --project Mezube.csproj
```

```
!play #voice never gonna give you up
!stream #radio https://example.com/audio.ogg
!queue / !np / !skip / !stop
!help
```

Channel target: mention hashtag kênh (`#voice`, `#radio`). Voice cũng fallback sang voice presence theo clan; stream fallback khi chạy lệnh trong Stream channel.

## Lệnh

| Lệnh | Ý nghĩa |
|------|---------|
| `!play [#voice] <url\|query>` | Phát vào **voice** |
| `!stream [#stream] <url\|query>` | Phát vào **streaming** |
| `!skip` `!stop` `!queue` `!np` | Điều khiển |
| `!ping` `!help` | Trợ giúp |

## Docker

Build từ thư mục cha (cần `Mezon.Net`):

```powershell
docker build -f Mezube/Dockerfile -t mezube .
docker run -e DOTNET_ENVIRONMENT=prod -v ${PWD}/Mezube/appsettings.prod.local.json:/app/appsettings.prod.local.json:ro mezube
```
