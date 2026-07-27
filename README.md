# Mezube

Bot phát nhạc cho [Mezon](https://mezon.ai) — prefix `!`, nguồn YouTube (`yt-dlp`) + direct URL.

- **Voice** (`!play`): STN voice v2 `POST /api/v2/voice/play|stop` + `GET /api/v2/voice/status` (wait until `publishing`)
- **Streaming** (`!stream`): STN WebSocket `/ws` `connect_publisher` / `stop_publisher`
- Legacy STN `/api/playmedia|/api/stopmedia` (URL_INPUT) is **not** used by Mezube anymore; kept on STN for other clients.

Bot chuẩn bị CDN **Ogg Opus 48 kHz**; STN không transcode (xem [mezon-media-station README](../mezon-media-station/README.md)).

## Yêu cầu

- .NET 10 SDK
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) trên PATH
- [ffmpeg](https://ffmpeg.org/) trên PATH (convert → ogg)
- Sibling repo: `F:\projects\mezon\Mezon.Net` (ProjectReference Sdk)

## Cấu hình

Hai profile riêng (secrets gitignore):

```powershell
copy .env.example .env
copy .env.example.dev .env.dev
copy .env.example.prod .env.prod
# điền credentials vào .env.dev / .env.prod
```

| File | Vai trò |
|------|---------|
| `.env` | Chọn profile: `MEZUBE_ENV=prod` hoặc `dev` |
| `.env.prod` / `.env.dev` | Credentials + host/STN cho từng môi trường |
| `.env.local` (optional) | Override máy local (ffmpeg path, …) — load sau cùng |

Đổi môi trường: sửa `MEZUBE_ENV` trong `.env` (hiện đang `prod`).

| Biến | Mô tả |
|------|--------|
| `MEZUBE_ENV` | `prod` hoặc `dev` |
| `MEZON_BOT_ID` / `MEZON_BOT_TOKEN` | Bot credentials |
| `MEZON_SERVER_KEY` | Gateway Basic-Auth — Dev `defaultkey`, Prod `HTTP3m3zonPr0dkey` |
| `MEZON_HOST` / `MEZON_PORT` | Dev `dev-mezon.nccsoft.vn:8088`, Prod `gw.mezon.ai:443` |
| `MEZUBE_STN_BASE_URL` | STN origin (vd. `https://stn.mezon.ai` / `http://localhost:8081`) — derive `/api/v2/voice/*` + `/ws` |
| `MEZUBE_CDN_BASE_URL` | Public CDN sau upload (vd. `https://cdn.komu.vn`) |
| `MEZUBE_BOT_AVATAR_URL` | Avatar bot — embed `author.icon_url` + thumbnail fallback khi track không có ảnh |
| `MEZUBE_VIZ_IMAGE_URL` / `MEZUBE_VIZ_POSITION_URL` | Equalizer sprite + JSON cho `!np` animation (để trống → auto-upload `Assets/viz`) |
| `MEZUBE_TRACKS_DB_PATH` | SQLite track library (mặc định `data/tracks.db`) |

## Chạy

```powershell
dotnet run --project Mezube.csproj
```

```
!play #voice never gonna give you up
!stream #radio https://example.com/audio.ogg
!queue / !np / !skip / !stop
!help
```

Channel target: mention hashtag kênh (`#voice`, `#radio`). Voice cũng fallback sang voice presence của bạn; stream fallback khi chạy lệnh trong Stream channel.

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
docker run --env-file Mezube/.env.prod mezube
```
