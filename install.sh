#!/usr/bin/env bash
# ctv installer - https://github.com/basdoas/ctv
#
#   curl -fsSL https://raw.githubusercontent.com/basdoas/ctv/main/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/basdoas/ctv/main/install.sh | bash -s -- --version 0.2.0
#
# Installs the prebuilt native binary from a GitHub release (checksum verified).
# With --from-source it builds ctv itself using the .NET SDK instead.

set -euo pipefail

REPO="basdoas/ctv"
NAME="ctv"
VERSION="latest"
PREFIX=""
FROM_SOURCE=0
ACTION="install"
PURGE=0

usage() {
  cat <<'EOF'
Usage: install.sh [options]

  --version X.Y.Z   install a specific release (default: the latest one)
  --prefix DIR      install under DIR/bin (default: ~/.local, or /usr/local when run as root)
  --from-source     build with the .NET SDK instead of downloading a binary
  --uninstall       remove ctv (your settings are kept)
  --purge           with --uninstall: also delete ~/.config/ctv (API keys, sessions)
  -h, --help        show this help
EOF
}

say()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --version)     [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
    --prefix)      [ $# -ge 2 ] || die "--prefix needs a value";  PREFIX="$2";  shift 2 ;;
    --from-source) FROM_SOURCE=1; shift ;;
    --uninstall)   ACTION="uninstall"; shift ;;
    --purge)       PURGE=1; shift ;;
    -h|--help)     usage; exit 0 ;;
    *)             usage >&2; die "unknown option: $1" ;;
  esac
done

if [ -z "$PREFIX" ]; then
  if [ "$(id -u)" -eq 0 ]; then PREFIX="/usr/local"; else PREFIX="${HOME}/.local"; fi
fi
BINDIR="${PREFIX}/bin"
TARGET="${BINDIR}/${NAME}"

# ── uninstall ───────────────────────────────────────────────────────────────
if [ "$ACTION" = "uninstall" ]; then
  if [ -e "$TARGET" ]; then
    rm -f "$TARGET"
    say "Removed $TARGET"
  else
    warn "$TARGET not found (was ctv installed with a different --prefix, or with dnf?)"
  fi
  CONF="${XDG_CONFIG_HOME:-$HOME/.config}/ctv"
  if [ "$PURGE" -eq 1 ]; then
    rm -rf "$CONF"
    say "Removed $CONF"
  else
    say "Your settings were kept in $CONF (use --purge to delete them too)."
  fi
  exit 0
fi

# ── platform ────────────────────────────────────────────────────────────────
[ "$(uname -s)" = "Linux" ] || die "this installer supports Linux only (found $(uname -s))"
case "$(uname -m)" in
  x86_64|amd64)  ARCH="x64" ;;
  aarch64|arm64) ARCH="arm64" ;;
  *)             die "unsupported CPU: $(uname -m)" ;;
esac

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

fetch() { # fetch URL DEST
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL --retry 3 -o "$2" "$1"
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$2" "$1"
  else
    die "curl or wget is required"
  fi
}

install_binary() { # install_binary PATH
  mkdir -p "$BINDIR"
  install -m 0755 "$1" "$TARGET"
  say "Installed $TARGET"
  case ":$PATH:" in
    *":$BINDIR:"*) ;;
    *) warn "$BINDIR is not in your PATH. Add this line to ~/.bashrc:  export PATH=\"$BINDIR:\$PATH\"" ;;
  esac
}

download_prebuilt() {
  local asset="ctv-linux-${ARCH}.tar.gz" base
  if [ "$VERSION" = "latest" ]; then
    base="https://github.com/${REPO}/releases/latest/download"
  else
    base="https://github.com/${REPO}/releases/download/v${VERSION#v}"
  fi

  say "Downloading ${asset} (${VERSION})"
  fetch "${base}/${asset}" "$TMP/${asset}" || die "download failed: ${base}/${asset}"

  if fetch "${base}/SHA256SUMS" "$TMP/SHA256SUMS" 2>/dev/null; then
    ( cd "$TMP" && grep " ${asset}\$" SHA256SUMS | sha256sum -c - >/dev/null ) \
      || die "checksum mismatch - refusing to install"
    say "Checksum OK"
  else
    warn "this release has no SHA256SUMS file; skipping verification"
  fi

  tar -xzf "$TMP/${asset}" -C "$TMP" || die "could not unpack ${asset}"
  [ -f "$TMP/ctv" ] || die "the archive does not contain the ctv binary"
  install_binary "$TMP/ctv"
}

build_from_source() {
  command -v dotnet >/dev/null 2>&1 \
    || die "building from source needs the .NET SDK (https://dotnet.microsoft.com/download)"
  command -v clang >/dev/null 2>&1 \
    || die "Native AOT needs clang. Fedora: sudo dnf install clang zlib-devel | Debian/Ubuntu: sudo apt install clang zlib1g-dev"

  local src="" here="${BASH_SOURCE[0]:-}"
  if [ -n "$here" ] && [ -f "$(dirname "$here")/ctv.csproj" ]; then
    src="$(cd "$(dirname "$here")" && pwd)"   # run from inside a checkout
  else
    command -v git >/dev/null 2>&1 || die "git is required to fetch the sources"
    local branch_args=()
    if [ "$VERSION" != "latest" ]; then branch_args=(--branch "v${VERSION#v}"); fi
    say "Cloning https://github.com/${REPO}"
    git clone --depth 1 ${branch_args[@]+"${branch_args[@]}"} "https://github.com/${REPO}.git" "$TMP/src" >/dev/null 2>&1 \
      || die "git clone failed"
    src="$TMP/src"
  fi

  say "Building the native binary (about a minute)"
  dotnet publish "$src/ctv.csproj" -c Release -r "linux-${ARCH}" -o "$TMP/out" >/dev/null \
    || die "build failed (run it without the installer to see the compiler output)"
  install_binary "$TMP/out/ctv"
}

# ── go ──────────────────────────────────────────────────────────────────────
if [ "$FROM_SOURCE" -eq 1 ]; then
  build_from_source
elif [ "$ARCH" != "x64" ]; then
  say "No prebuilt binary for ${ARCH} yet - building from source"
  build_from_source
else
  download_prebuilt
fi

say "Done. Start it with:  ctv"
if [ -f /etc/fedora-release ]; then
  say "Fedora tip: the dnf repository gives you updates with 'sudo dnf upgrade' (see the README)."
fi
