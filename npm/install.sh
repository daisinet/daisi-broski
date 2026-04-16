#!/usr/bin/env bash
# One-command installer for daisi-broski-skim.
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/daisinet/daisi-broski/main/npm/install.sh | bash
#
# Works on Linux / macOS. Requires Node 16+ with npm on PATH.
# Falls back with a clear error message if npm is missing.

set -euo pipefail

PKG="@daisinet/broski"

if ! command -v npm >/dev/null 2>&1; then
    echo "❌ npm not found." >&2
    echo "   Install Node.js (which ships with npm) from https://nodejs.org," >&2
    echo "   or via your package manager:" >&2
    echo "     macOS:  brew install node" >&2
    echo "     Ubuntu: curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash - && sudo apt-get install -y nodejs" >&2
    exit 1
fi

# npm -g defaults to a root-owned prefix on some distros; flag that
# clearly instead of erroring cryptically.
if [ "$(id -u)" -ne 0 ] && [ "$(npm config get prefix)" = "/usr" ]; then
    echo "⚠  Global install prefix is /usr — you'll likely need sudo." >&2
    echo "   Rerun with:  curl -fsSL <this url> | sudo bash" >&2
    echo "   Or set a user-local prefix:  npm config set prefix ~/.npm-global" >&2
fi

echo "Installing $PKG via npm…"
npm install -g "$PKG"

echo ""
echo "Installed. Try:"
echo "   broski https://en.wikipedia.org/wiki/JavaScript --format md"
