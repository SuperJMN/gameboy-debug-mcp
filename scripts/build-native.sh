#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SAMEBOY_DIR="${SAMEBOY_DIR:-$ROOT_DIR/external/SameBoy}"
SAMEBOY_REPO="${SAMEBOY_REPO:-https://github.com/LIJI32/SameBoy.git}"
OUT_DIR="$ROOT_DIR/native/out/linux-x64"

if [ ! -d "$SAMEBOY_DIR/.git" ]; then
  rm -rf "$SAMEBOY_DIR"
  git clone --depth 1 "$SAMEBOY_REPO" "$SAMEBOY_DIR"
fi

make -C "$SAMEBOY_DIR" lib CONF=release

mkdir -p "$OUT_DIR"
cc -shared -fPIC \
  "$ROOT_DIR/native/sameboy_mcp_bridge.c" \
  -DGB_INTERNAL \
  -I"$SAMEBOY_DIR/Core" \
  -I"$SAMEBOY_DIR" \
  -L"$SAMEBOY_DIR/build/lib" \
  -Wl,-rpath,'$ORIGIN' \
  -lsameboy \
  -o "$OUT_DIR/libgameboy_debug_sameboy.so"

cp "$SAMEBOY_DIR/build/lib/libsameboy.so" "$OUT_DIR/libsameboy.so"
printf '%s\n' "$OUT_DIR"
