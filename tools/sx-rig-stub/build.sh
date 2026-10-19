#!/usr/bin/env bash
# Builds the STUB libsx_rig.so (see sx_rig_stub.c) for arm64-v8a and for the host.
#   SX_RIG_INCLUDE  directory holding sx_rig.h (the sx monorepo's
#                   experience/data-factory/quest-rig/include)
#   ANDROID_NDK     NDK root (default ~/Android/Sdk/ndk/26.1.10909125)
# Output: build/arm64-v8a/libsx_rig.so and build/host/libsx_rig.so. Nothing is copied into
# Assets/: the shipped APK carries the real Rust library or none. To try the stub on a headset,
# copy build/arm64-v8a/libsx_rig.so to Assets/Plugins/Android/libs/arm64-v8a/ by hand and
# never commit it.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
include="${SX_RIG_INCLUDE:?set SX_RIG_INCLUDE to the directory holding sx_rig.h}"
ndk="${ANDROID_NDK:-$HOME/Android/Sdk/ndk/26.1.10909125}"
clang="$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android32-clang"
mkdir -p "$here/build/arm64-v8a" "$here/build/host"
"$clang" -shared -fPIC -O2 -Wall -Wextra -Werror -I"$include" -Wl,-soname,libsx_rig.so \
    -o "$here/build/arm64-v8a/libsx_rig.so" "$here/sx_rig_stub.c"
cc -shared -fPIC -O2 -Wall -Wextra -Werror -I"$include" \
    -o "$here/build/host/libsx_rig.so" "$here/sx_rig_stub.c"
file "$here/build/arm64-v8a/libsx_rig.so" "$here/build/host/libsx_rig.so"
