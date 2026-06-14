#!/usr/bin/env bash
set -euo pipefail

APP_NAME="codebase-memory-wrapper"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INSTALL_ROOT="${CODEBASE_MEMORY_WRAPPER_INSTALL_ROOT:-"$HOME/.local/share/$APP_NAME"}"
APP_DIR="$INSTALL_ROOT/app"
ENV_FILE="$INSTALL_ROOT/$APP_NAME.env"
UNIT_DIR="$HOME/.config/systemd/user"
UNIT_FILE="$UNIT_DIR/$APP_NAME.service"
DEFAULT_CHILD="$HOME/.local/bin/codebase-memory-mcp"
DOTNET_BIN="${CODEBASE_MEMORY_WRAPPER_DOTNET:-"$(command -v dotnet || true)"}"

escape_env_value() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    printf '"%s"' "$value"
}

detect_child_command() {
    local configured="${CODEBASE_MEMORY_WRAPPER_CHILD_COMMAND:-}"

    if [[ -n "$configured" ]]; then
        printf '%s\n' "$configured"
        return
    fi

    if [[ -x "$DEFAULT_CHILD" ]]; then
        printf '%s\n' "$DEFAULT_CHILD"
        return
    fi

    read -r -p "Path to codebase-memory-mcp: " configured
    printf '%s\n' "$configured"
}

CHILD_COMMAND="$(detect_child_command)"

if [[ ! -x "$CHILD_COMMAND" ]]; then
    echo "Child command does not exist or is not executable: $CHILD_COMMAND" >&2
    exit 1
fi

if [[ -z "$DOTNET_BIN" || ! -x "$DOTNET_BIN" ]]; then
    echo "dotnet executable was not found. Set CODEBASE_MEMORY_WRAPPER_DOTNET." >&2
    exit 1
fi

if [[ "$INSTALL_ROOT" =~ [[:space:]] || "$DOTNET_BIN" =~ [[:space:]] ]]; then
    echo "Install root and dotnet path must not contain whitespace for the systemd unit." >&2
    exit 1
fi

mkdir -p "$APP_DIR" "$UNIT_DIR"
"$DOTNET_BIN" publish "$REPO_ROOT/codebase-memory-wrapper.csproj" \
    --configuration Release \
    --output "$APP_DIR" \
    --self-contained false

cat > "$ENV_FILE" <<ENVEOF
Wrapper__Child__Command=$(escape_env_value "$CHILD_COMMAND")
DOTNET_ENVIRONMENT=Production
ENVEOF

cat > "$UNIT_FILE" <<UNITEOF
[Unit]
Description=Codebase Memory MCP wrapper
After=network.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
EnvironmentFile=$ENV_FILE
ExecStart=$DOTNET_BIN $APP_DIR/codebase-memory-wrapper.dll
Restart=on-failure
RestartSec=5s

[Install]
WantedBy=default.target
UNITEOF

systemctl --user daemon-reload
systemctl --user enable --now "$APP_NAME.service"

echo "Installed and started $APP_NAME.service"
echo "MCP endpoint: http://127.0.0.1:39749/mcp"
