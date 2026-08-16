# Mezube

Bot phát nhạc cho [Mezon](https://mezon.ai) — prefix `!`, nguồn YouTube (`yt-dlp`) + SoundCloud + direct URL.

- **Streaming only** (`!play`): STN WebSocket `/ws` `connect_publisher` / `stop_publisher` với CDN **WebM** (Opus+VP8, GOP ~2s) hoặc Ogg fallback
- STN **không** còn `/api/voice/*` hay `/api/whip/*`. Voice/Gmeet channel không phải publish target.

Bot encode trước khi publish; STN passthrough, không ffmpeg (xem [mezon-media-server](../mezon-media-server/docs/PROTOCOL.md)).

## Yêu cầu

- .NET 10 SDK (deploy) / .NET 10 runtime (chạy framework-dependent)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) trên PATH
- [ffmpeg](https://ffmpeg.org/) trên PATH (Ogg Opus + WebM Opus+VP8 GOP 2s cho STN passthrough)
- NuGet: `Mezon.Net.Sdk` (không cần sibling `Mezon.Net` source)

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
| `Mezube:StnBaseUrl` | STN origin (vd. `https://stn.mezon.ai`) — derive `ws(s)://…/ws` |
| `Mezube:PreparedAudioBitrateKbps` / `PreparedAudioChannels` / `PreparedAudioSampleRate` | Preset cho file CDN audio (Ogg Opus) |
| `Mezube:PreparedVideoBitrateKbps` / `PreparedVideoHeight` / `PreparedVideoFps` | Streaming WebM VP8 (GOP = 2×fps, STN `max_keyframe_gap_ms` 2500) |
| `Mezube:CdnBaseUrl` | Public CDN sau upload |
| `Mezube:BotAvatarUrl` | Avatar bot — embed author + thumbnail fallback |
| `Mezube:VizImageUrl` / `Mezube:VizPositionUrl` | Equalizer sprite + JSON cho `!np` |
| `Mezube:TracksDbPath` | SQLite track library (mặc định `data/tracks.db`) |

### Preset gợi ý

- Audio: `PreparedAudioBitrateKbps=128`
- Video: `PreparedVideoBitrateKbps=1000`, `PreparedVideoHeight=720`, `PreparedVideoFps=30` (GOP 2s)

Shutdown bot cancel pump + dispose mọi STN WS publisher.

## Chạy

```powershell
$env:DOTNET_ENVIRONMENT='dev'
dotnet run --project Mezube.csproj
```

```
!play #radio never gonna give you up
!play https://example.com/audio.ogg
!queue / !np / !skip / !stop
!setdj @DJ / !settings
!help
```

Channel target: mention hashtag kênh stream (`#radio`). Fallback: lệnh trong Stream channel, rồi default stream channel của clan. Tag voice/Gmeet → reject.

### Clan invite (mid-session)

Khi bot đang chạy và được invite vào clan mới, SDK `ClanJoined` kích hoạt `RefreshClanMembershipAsync` (debounce ~3s) để `JoinClanChat` — không cần restart. Log: `Clan joined mid-session clanId=…; refreshing membership`.

### DJ permissions

Mô hình kiểu JMusicBot (chưa có vote-skip):

| | Everyone | Track requester | DJ role / clan owner |
|--|--|--|--|
| `play` / `queue` / `np` / `help` | yes | yes | yes |
| `skip` own current track | — | yes | yes |
| force skip / `stop` / Skip·Stop buttons (others) | no | no | yes |
| `setdj` | no | no | owner only |

`!setdj @role|roleId|none` — chỉ clan owner. `!settings` hiện DJ role hiện tại.

### Playback / queue limits

| Limit | Default | Ý nghĩa |
|-------|---------|---------|
| Max queue / clan | 20 | Quá → title `Everyone wants a piece of me today! Please take a number and hold tight` |
| Prep concurrency | 64 | Download/ffmpeg/CDN song song toàn bot |
| Concurrent playback | 64 | Tối đa 64 clan đang phát |
| Max audio size | 200MB | CDN Ogg |
| Max video size | 500MB | Streaming WebM |
| Inter-track delay | 2s | Sau mỗi lần stop sink trước bài kế |

Audio được **process ngầm** ngay khi vào queue (không đợi đến lượt phát). Persistence: **PostgreSQL** (track library, clan settings, playlists, history) + **Redis** (player/queue session). Xem `docker-compose.yml` và `Mezube:PostgresConnectionString` / `Mezube:RedisConnectionString`.

## Lệnh

| Lệnh | Ý nghĩa |
|------|---------|
| `!play [#stream] <url\|query>` | Phát vào **stream channel** |
| `!skip` `!stop` `!queue` `!np` | Điều khiển (skip/stop theo DJ rules) |
| `!playlist default <name\|none>` | Playlist mặc định (DJ/owner); idle 5 phút tự phát lại trên default stream |
| `!setdj` `!settings` | Cấu hình DJ role |
| `!help` | Trợ giúp |

## Docker

Build từ thư mục `Mezube` (NuGet `Mezon.Net.Sdk`, không cần sibling source). Image biên dịch **FFmpeg 8.0.1** từ `Assets/ffmpeg/ffmpeg_8.0.1.orig.tar.xz` với OpenSSL (HTTPS), libopus, libvpx, và codec decode phổ biến (libx264 / libx265 / libaom·libdav1d·libsvtav1 AV1).

```powershell
docker build -t mezube .
docker run --rm -e DOTNET_ENVIRONMENT=prod `
  -v ${PWD}/appsettings.prod.local.json:/app/appsettings.prod.local.json:ro `
  -v mezube-data:/app/data -v mezube-temp:/app/temp mezube
```

```bash
docker build -t mezube .
docker run --rm -e DOTNET_ENVIRONMENT=prod \
  -v "$PWD/appsettings.prod.local.json:/app/appsettings.prod.local.json:ro" \
  -v mezube-data:/app/data -v mezube-temp:/app/temp mezube
```

## Deploy production (Windows + Linux)

Script publish Release, giữ `data/`, tạo `run.ps1` / `run.sh`, và (Linux) unit systemd.

**Windows (PowerShell):**

```powershell
./scripts/deploy-prod.ps1
./scripts/deploy-prod.ps1 -SelfContained
./scripts/deploy-prod.ps1 -Run
./scripts/deploy-prod.ps1 -Stop
./scripts/deploy-prod.ps1 -SkipPublish -Start
```

**Linux / macOS:**

```bash
chmod +x scripts/deploy-prod.sh
./scripts/deploy-prod.sh
./scripts/deploy-prod.sh --self-contained --output-dir /opt/mezube
./scripts/deploy-prod.sh --install-service --start   # cần sudo + systemd
./scripts/deploy-prod.sh --stop
```

Cross-compile ví dụ (build trên Windows cho Linux server):

```powershell
./scripts/deploy-prod.ps1 -Runtime linux-x64 -SelfContained -OutputDir .\publish\linux
# copy publish/linux lên server, rồi: ./run.sh
```

Output mặc định: `publish/prod`. Secrets nên đặt `appsettings.prod.local.json` (gitignore) cạnh DLL hoặc dùng env `Mezon__Token`, …