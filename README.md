# Mezube

Bot phát nhạc cho [Mezon](https://mezon.ai) — prefix `!`, nguồn YouTube (`yt-dlp`) + SoundCloud + direct URL.

- **Streaming only** (`!play`): SFU WebSocket + publisher WebRTC in-process, với CDN **Ogg Opus 48 kHz stereo**.
- SFU là media transport duy nhất cho audio của stream channel và Mezon voice channel. Mezube chỉ publish audio vào stream channel.

Bot chuẩn hóa audio bằng FFmpeg, upload CDN, rồi Mezube đọc Ogg/Opus và gửi RTP audio qua SFU.

## Yêu cầu

- .NET 10 SDK (deploy) / .NET 10 runtime (chạy framework-dependent)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) trên PATH
- [ffmpeg](https://ffmpeg.org/) trên PATH (chuẩn hóa Ogg Opus cho SFU publisher)
- NuGet: `Mezon.Net.Sdk` và `SIPSorcery` (publisher WebRTC chạy trực tiếp trong Mezube)

## Cấu hình

Chọn môi trường bằng `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` = `dev` | `prod`.

| File | Vai trò |
|------|---------|
| `appsettings.json` | Shared defaults (prefix, paths, viz/CDN URLs, logging) — **không** chứa token |
| `appsettings.dev.json` / `appsettings.prod.json` | Host / SFU / ServerKey theo môi trường |
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
| `Mezube:SfuWebSocketUrl` | SFU signaling endpoint `ws://` hoặc `wss://`; bắt buộc cấu hình khi deploy |
| `Mezube:SfuConnectTimeoutMs` | Timeout kết nối WebSocket/WebRTC publisher |
| `Mezube:SfuReconnectBackoffMs` / `SfuReconnectMaxBackoffMs` | Exponential reconnect backoff cơ sở và trần tối đa |
| `Mezube:SfuReconnectMaxAttempts` | Số lần reconnect liên tiếp tối đa trước khi session báo lỗi và dừng retry |
| `Mezube:SfuReconnectJitterMs` / `Mezube:SfuReconnectStableResetMs` | Jitter chống retry đồng bộ và khoảng kết nối ổn định để reset bộ đếm |
| `Mezube:SfuMaxSessions` | Giới hạn stream session đồng thời |

| `Mezube:PreparedAudioBitrateKbps` | Bitrate cho file CDN audio (Ogg Opus) |
| `Mezube:PreparedAudioChannels` / `Mezube:PreparedAudioSampleRate` | Cấu hình media preparation; audio wire của SFU luôn cố định 2 kênh / 48 kHz |
| `Mezube:PreparedVideoBitrateKbps` / `PreparedVideoHeight` / `PreparedVideoFps` | Legacy video preparation; Mezube music không publish video vào SFU |
| `Mezube:CdnBaseUrl` | Public CDN sau upload |
| `Mezube:BotAvatarUrl` | Avatar bot — embed author + thumbnail fallback |
| `Mezube:VizImageUrl` / `Mezube:VizPositionUrl` | Equalizer sprite + JSON cho `!np` |
| `Mezube:TracksDbPath` | SQLite track library (mặc định `data/tracks.db`) |

### Preset gợi ý

- Audio SFU: `PreparedAudioBitrateKbps=128`, wire format cố định Ogg Opus 48 kHz stereo
- Video: `PreparedVideoBitrateKbps=1000`, `PreparedVideoHeight=720`, `PreparedVideoFps=30` (GOP 2s)

Shutdown bot cancel pump + dispose mọi SFU publisher session.

Playback audio published to SFU is always normalized by FFmpeg to Ogg/Opus,
48 kHz stereo; publisher chỉ nhận URL audio đã chuẩn hóa và không tạo/gửi video track.

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
