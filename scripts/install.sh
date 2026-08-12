#!/usr/bin/env bash
set -euo pipefail

VERSION="latest"
INSTALL_DIR="${HOME}/.local/bin"
ARCHIVE_PATH=""
CHECKSUMS_PATH=""

usage() {
  cat <<'EOF'
ERP Doctor installer for Linux x64

Usage:
  ./scripts/install.sh
  ./scripts/install.sh --version v0.15.0
  ./scripts/install.sh --install-dir /opt/erp-doctor/bin

Offline/CI validation:
  ./scripts/install.sh --archive <tar.gz> --checksums <checksums.txt> --install-dir <dir>

Options:
  --version <tag|latest>   GitHub Release tag (default: latest). 0.15.0 is normalized to v0.15.0.
  --install-dir <path>     Destination directory (default: ~/.local/bin).
  --archive <path>         Use a local release tar.gz instead of downloading.
  --checksums <path>       Use a local checksums.txt instead of downloading.
  -h, --help               Show this help.
EOF
}

while (($#)); do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || { echo "--version requires a value" >&2; exit 2; }
      VERSION="$2"
      shift 2
      ;;
    --install-dir)
      [[ $# -ge 2 ]] || { echo "--install-dir requires a value" >&2; exit 2; }
      INSTALL_DIR="$2"
      shift 2
      ;;
    --archive)
      [[ $# -ge 2 ]] || { echo "--archive requires a value" >&2; exit 2; }
      ARCHIVE_PATH="$2"
      shift 2
      ;;
    --checksums)
      [[ $# -ge 2 ]] || { echo "--checksums requires a value" >&2; exit 2; }
      CHECKSUMS_PATH="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

[[ "$(uname -s)" == "Linux" ]] || {
  echo "install.sh supports Linux only. Use scripts/install.ps1 on Windows." >&2
  exit 1
}

case "$(uname -m)" in
  x86_64|amd64) ;;
  *)
    echo "ERP Doctor self-contained releases currently support Linux x64 only." >&2
    exit 1
    ;;
esac

if [[ -n "$ARCHIVE_PATH" && -z "$CHECKSUMS_PATH" ]] || [[ -z "$ARCHIVE_PATH" && -n "$CHECKSUMS_PATH" ]]; then
  echo "--archive and --checksums must be supplied together." >&2
  exit 2
fi

for command_name in sha256sum tar; do
  command -v "$command_name" >/dev/null 2>&1 || {
    echo "Required command not found: $command_name" >&2
    exit 1
  }
done

ASSET_NAME="erp-doctor-linux-x64.tar.gz"
REPOSITORY="Longdotnet/erp-doctor"
TEMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/erp-doctor-install.XXXXXX")"
DOWNLOADED_ARCHIVE="$TEMP_ROOT/$ASSET_NAME"
DOWNLOADED_CHECKSUMS="$TEMP_ROOT/checksums.txt"
EXTRACT_DIR="$TEMP_ROOT/extract"

cleanup() {
  rm -rf "$TEMP_ROOT"
}
trap cleanup EXIT
mkdir -p "$EXTRACT_DIR"

release_base_url() {
  local requested="$1"
  if [[ "${requested,,}" == "latest" ]]; then
    printf 'https://github.com/%s/releases/latest/download' "$REPOSITORY"
    return
  fi

  local tag="$requested"
  [[ "$tag" == v* ]] || tag="v$tag"
  printf 'https://github.com/%s/releases/download/%s' "$REPOSITORY" "$tag"
}

if [[ -n "$ARCHIVE_PATH" ]]; then
  ARCHIVE_PATH="$(cd "$(dirname "$ARCHIVE_PATH")" && pwd)/$(basename "$ARCHIVE_PATH")"
  CHECKSUMS_PATH="$(cd "$(dirname "$CHECKSUMS_PATH")" && pwd)/$(basename "$CHECKSUMS_PATH")"
  [[ -f "$ARCHIVE_PATH" ]] || { echo "Archive not found: $ARCHIVE_PATH" >&2; exit 1; }
  [[ -f "$CHECKSUMS_PATH" ]] || { echo "Checksums file not found: $CHECKSUMS_PATH" >&2; exit 1; }
else
  command -v curl >/dev/null 2>&1 || {
    echo "Required command not found: curl" >&2
    exit 1
  }
  BASE_URL="$(release_base_url "$VERSION")"
  echo "Downloading ERP Doctor Linux x64 release..."
  curl --fail --location --silent --show-error "$BASE_URL/$ASSET_NAME" --output "$DOWNLOADED_ARCHIVE"
  curl --fail --location --silent --show-error "$BASE_URL/checksums.txt" --output "$DOWNLOADED_CHECKSUMS"
  ARCHIVE_PATH="$DOWNLOADED_ARCHIVE"
  CHECKSUMS_PATH="$DOWNLOADED_CHECKSUMS"
fi

EXPECTED_SHA="$(awk -v file="$ASSET_NAME" '$2 == file || $2 == "*" file {print tolower($1); exit}' "$CHECKSUMS_PATH")"
[[ "$EXPECTED_SHA" =~ ^[0-9a-f]{64}$ ]] || {
  echo "checksums.txt does not contain a valid SHA256 entry for $ASSET_NAME." >&2
  exit 1
}

ACTUAL_SHA="$(sha256sum "$ARCHIVE_PATH" | awk '{print tolower($1)}')"
if [[ "$ACTUAL_SHA" != "$EXPECTED_SHA" ]]; then
  echo "SHA256 verification failed for $ASSET_NAME." >&2
  echo "Expected: $EXPECTED_SHA" >&2
  echo "Actual:   $ACTUAL_SHA" >&2
  exit 1
fi

echo "SHA256 verified: $ACTUAL_SHA"
tar -xzf "$ARCHIVE_PATH" -C "$EXTRACT_DIR"

SOURCE_BINARY=""
for candidate in "$EXTRACT_DIR/ErpDoctor.Cli" "$EXTRACT_DIR/erp-doctor"; do
  if [[ -f "$candidate" ]]; then
    SOURCE_BINARY="$candidate"
    break
  fi
done

[[ -n "$SOURCE_BINARY" ]] || {
  echo "Release archive does not contain ErpDoctor.Cli or erp-doctor." >&2
  exit 1
}

mkdir -p "$INSTALL_DIR"
DESTINATION="$INSTALL_DIR/erp-doctor"
install -m 0755 "$SOURCE_BINARY" "$DESTINATION"

echo "ERP Doctor installed: $DESTINATION"
if [[ ":$PATH:" == *":$INSTALL_DIR:"* ]]; then
  echo "Run: erp-doctor --help"
else
  echo "Add this directory to PATH, then run erp-doctor --help:"
  echo "  $INSTALL_DIR"
fi
