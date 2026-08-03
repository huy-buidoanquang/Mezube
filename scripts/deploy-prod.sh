#!/usr/bin/env bash
# Production deploy wrapper for Linux/macOS.
# Prefer PowerShell Core when available; otherwise runs an equivalent bash publish flow.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

OUTPUT_DIR="${OUTPUT_DIR:-$ROOT/publish/prod}"
RUNTIME="${RUNTIME:-auto}"
CONFIGURATION="${CONFIGURATION:-Release}"
SELF_CONTAINED=0
SKIP_CHECKS=0
SKIP_PUBLISH=0
RUN=0
INSTALL_SERVICE=0
START=0
STOP=0
SERVICE_NAME="${SERVICE_NAME:-mezube}"

usage() {
  cat <<'EOF'
Usage: ./scripts/deploy-prod.sh [options]

Options:
  --output-dir DIR       Publish output (default: ./publish/prod)
  --runtime RID          auto|linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64
  --self-contained       Publish self-contained binary
  --skip-checks          Skip ffmpeg/yt-dlp/.NET checks
  --skip-publish         Reuse existing publish dir
  --run                  Run bot in foreground after publish
  --install-service      Install systemd unit (Linux, requires sudo)
  --start                Start after publish (systemd if --install-service)
  --stop                 Stop running bot/service
  --service-name NAME    systemd/pid name (default: mezube)
  -h, --help             Show help

Examples:
  ./scripts/deploy-prod.sh
  ./scripts/deploy-prod.sh --self-contained --output-dir /opt/mezube
  ./scripts/deploy-prod.sh --install-service --start
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
    --runtime) RUNTIME="$2"; shift 2 ;;
    --configuration) CONFIGURATION="$2"; shift 2 ;;
    --self-contained) SELF_CONTAINED=1; shift ;;
    --skip-checks) SKIP_CHECKS=1; shift ;;
    --skip-publish) SKIP_PUBLISH=1; shift ;;
    --run) RUN=1; shift ;;
    --install-service) INSTALL_SERVICE=1; shift ;;
    --start) START=1; shift ;;
    --stop) STOP=1; shift ;;
    --service-name) SERVICE_NAME="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

# Delegate to PowerShell when available (single source of truth).
if command -v pwsh >/dev/null 2>&1; then
  args=(-File "$ROOT/scripts/deploy-prod.ps1" -OutputDir "$OUTPUT_DIR" -Runtime "$RUNTIME" -Configuration "$CONFIGURATION" -ServiceName "$SERVICE_NAME")
  [[ $SELF_CONTAINED -eq 1 ]] && args+=(-SelfContained)
  [[ $SKIP_CHECKS -eq 1 ]] && args+=(-SkipChecks)
  [[ $SKIP_PUBLISH -eq 1 ]] && args+=(-SkipPublish)
  [[ $RUN -eq 1 ]] && args+=(-Run)
  [[ $INSTALL_SERVICE -eq 1 ]] && args+=(-InstallService)
  [[ $START -eq 1 ]] && args+=(-Start)
  [[ $STOP -eq 1 ]] && args+=(-Stop)
  exec pwsh -NoProfile "${args[@]}"
fi

echo "==> pwsh not found; using bash fallback"
need() { command -v "$1" >/dev/null 2>&1 || { echo "Missing required command: $1" >&2; exit 1; }; }

if [[ $SKIP_CHECKS -eq 0 ]]; then
  need dotnet
  command -v ffmpeg >/dev/null 2>&1 || echo "WARN ffmpeg not on PATH"
  command -v yt-dlp >/dev/null 2>&1 || echo "WARN yt-dlp not on PATH"
fi

detect_runtime() {
  local arch
  arch="$(uname -m)"
  case "$(uname -s)" in
    Linux)
      case "$arch" in
        aarch64|arm64) echo linux-arm64 ;;
        *) echo linux-x64 ;;
      esac
      ;;
    Darwin)
      case "$arch" in
        arm64) echo osx-arm64 ;;
        *) echo osx-x64 ;;
      esac
      ;;
    MINGW*|MSYS*|CYGWIN*)
      echo win-x64
      ;;
    *)
      echo "Unsupported OS for auto runtime" >&2
      exit 1
      ;;
  esac
}

[[ "$RUNTIME" == "auto" ]] && RUNTIME="$(detect_runtime)"
OUTPUT_DIR="$(mkdir -p "$OUTPUT_DIR" && cd "$OUTPUT_DIR" && pwd)"

stop_bot() {
  local pid_file="$OUTPUT_DIR/$SERVICE_NAME.pid"
  if [[ -f "$pid_file" ]]; then
    local pid
    pid="$(tr -d '[:space:]' < "$pid_file" || true)"
    if [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null; then
      echo "==> Stopping pid $pid"
      kill "$pid" || true
      sleep 1
      kill -9 "$pid" 2>/dev/null || true
    fi
    rm -f "$pid_file"
  fi
  if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    echo "==> Stopping systemd $SERVICE_NAME"
    sudo systemctl stop "$SERVICE_NAME"
  fi
}

[[ $STOP -eq 1 ]] && stop_bot

DATA_BACKUP=""
if [[ $SKIP_PUBLISH -eq 0 && -d "$OUTPUT_DIR/data" ]]; then
  DATA_BACKUP="$(mktemp -d)"
  echo "==> Backing up data -> $DATA_BACKUP"
  cp -a "$OUTPUT_DIR/data/." "$DATA_BACKUP/"
fi

if [[ $SKIP_PUBLISH -eq 0 ]]; then
  [[ $START -eq 1 || $RUN -eq 1 || $INSTALL_SERVICE -eq 1 ]] && stop_bot
  echo "==> Publishing ($CONFIGURATION / $RUNTIME)"
  publish_args=(publish Mezube.csproj -c "$CONFIGURATION" -r "$RUNTIME" -o "$OUTPUT_DIR" --nologo
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false)
  if [[ $SELF_CONTAINED -eq 1 ]]; then
    publish_args+=(--self-contained true -p:PublishSingleFile=false)
  else
    publish_args+=(--self-contained false)
  fi
  dotnet "${publish_args[@]}"
fi

mkdir -p "$OUTPUT_DIR/data" "$OUTPUT_DIR/temp"
if [[ -n "$DATA_BACKUP" ]]; then
  echo "==> Restoring data"
  cp -a "$DATA_BACKUP/." "$OUTPUT_DIR/data/"
  rm -rf "$DATA_BACKUP"
fi

for local_name in appsettings.prod.local.json appsettings.local.json; do
  if [[ -f "$ROOT/$local_name" && ! -f "$OUTPUT_DIR/$local_name" ]]; then
    cp "$ROOT/$local_name" "$OUTPUT_DIR/$local_name"
    echo "OK  Copied $local_name"
  fi
done

USE_APPHOST=0
[[ -x "$OUTPUT_DIR/Mezube" || -f "$OUTPUT_DIR/Mezube" ]] && USE_APPHOST=1
[[ $SELF_CONTAINED -eq 1 ]] && USE_APPHOST=1

cat > "$OUTPUT_DIR/run.sh" <<EOF
#!/usr/bin/env bash
set -euo pipefail
cd "\$(dirname "\$0")"
export DOTNET_ENVIRONMENT=prod
mkdir -p data temp
EOF
if [[ $USE_APPHOST -eq 1 ]]; then
  cat >> "$OUTPUT_DIR/run.sh" <<'EOF'
chmod +x ./Mezube 2>/dev/null || true
exec ./Mezube "$@"
EOF
  chmod +x "$OUTPUT_DIR/Mezube" 2>/dev/null || true
else
  cat >> "$OUTPUT_DIR/run.sh" <<'EOF'
exec dotnet ./Mezube.dll "$@"
EOF
fi
chmod +x "$OUTPUT_DIR/run.sh"

if [[ $USE_APPHOST -eq 1 ]]; then
  EXEC_START="$OUTPUT_DIR/Mezube"
else
  EXEC_START="/usr/bin/dotnet $OUTPUT_DIR/Mezube.dll"
fi

cat > "$OUTPUT_DIR/$SERVICE_NAME.service" <<EOF
[Unit]
Description=Mezube Mezon music bot
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$OUTPUT_DIR
Environment=DOTNET_ENVIRONMENT=prod
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=$EXEC_START
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF

if [[ $INSTALL_SERVICE -eq 1 ]]; then
  need systemctl
  need sudo
  echo "==> Installing systemd unit"
  sudo cp "$OUTPUT_DIR/$SERVICE_NAME.service" "/etc/systemd/system/$SERVICE_NAME.service"
  sudo systemctl daemon-reload
  sudo systemctl enable "$SERVICE_NAME.service"
fi

if [[ $START -eq 1 ]]; then
  if [[ $INSTALL_SERVICE -eq 1 ]]; then
    sudo systemctl restart "$SERVICE_NAME"
    sudo systemctl --no-pager --full status "$SERVICE_NAME"
  else
    echo "==> Starting in background"
    nohup "$OUTPUT_DIR/run.sh" >"$OUTPUT_DIR/mezube.out.log" 2>&1 &
    echo $! >"$OUTPUT_DIR/$SERVICE_NAME.pid"
    echo "OK  pid=$(cat "$OUTPUT_DIR/$SERVICE_NAME.pid") log=$OUTPUT_DIR/mezube.out.log"
  fi
fi

if [[ $RUN -eq 1 ]]; then
  echo "==> Running in foreground"
  exec "$OUTPUT_DIR/run.sh"
fi

echo "OK  Deploy ready: $OUTPUT_DIR"
echo "Start: $OUTPUT_DIR/run.sh"
