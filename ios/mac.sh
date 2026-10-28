#!/bin/bash
# 在 Mac 上编译 TailOrbit 的 iPhone App、装到手机。把 ios 目录拷到 Mac 上，在里面跑：
#   ./mac.sh build            生成工程、编给真机（有别的 xcodebuild 在跑就先等）
#   ./mac.sh devinstall       经 Mac 的图形界面会话编译并装到手机：签名只有在那里才行，SSH 里会失败
#   ./mac.sh install          把上次编好的装到手机
#   ./mac.sh run [秒]         在手机上启动，打印它的日志（手机要解锁）
#   ./mac.sh sim [秒] [参数]  编给模拟器、装上、启动，打印日志；参数传给 App，例如 -url http://127.0.0.1:8723/a/test/
# 签名团队、收听网址、手机编号写在 Local.xcconfig（见 Config.xcconfig 开头）
set -u
export PATH=$HOME/.local/bin:$PATH LANG=en_US.UTF-8
cd "$(dirname "$0")" || exit 1

setting() { sed -n "s/^$1 *= *//p" Local.xcconfig Config.xcconfig 2>/dev/null | head -1; }
DEVICE=$(setting TAILORBIT_DEVICE)
BUNDLE=$(setting PRODUCT_BUNDLE_IDENTIFIER)
APP=build/dd/Build/Products/Debug-iphoneos/TailOrbit.app
SIM_NAME="TailOrbit iPhone"

wait_free() {
  for _ in $(seq 1 40); do
    pgrep -x xcodebuild >/dev/null || return 0
    sleep 30
  done
  echo "another xcodebuild has been running for 20 minutes; stopping" >&2
  return 1
}

sim_id() {
  local id
  id=$(xcrun simctl list devices available | sed -n "s/^ *$SIM_NAME (\([0-9A-F-]*\)).*/\1/p" | head -1)
  [ -n "$id" ] || id=$(xcrun simctl create "$SIM_NAME" com.apple.CoreSimulator.SimDeviceType.iPhone-17)
  echo "$id"
}

case "${1:-}" in
  build)
    wait_free || exit 1
    xcodegen generate --spec project.yml >/dev/null || exit 1
    mkdir -p build
    caffeinate -i xcodebuild -project TailOrbit.xcodeproj -scheme TailOrbit -configuration Debug \
      -destination generic/platform=iOS -derivedDataPath build/dd -allowProvisioningUpdates build > build/last-build.log 2>&1
    status=$?
    grep -E 'error:|warning:|BUILD (SUCCEEDED|FAILED)|requires a provisioning|No Accounts|errSec' build/last-build.log | sort -u | head -60
    exit $status
    ;;
  devinstall)
    mkdir -p build
    cmd="$PWD/build/devinstall.command"
    log="$PWD/build/devinstall.log"
    rm -f "$log"
    printf '#!/bin/zsh\ncd "%s" && { ./mac.sh build && ./mac.sh install; echo "exit $?"; } > "%s" 2>&1\necho DEVINSTALL-DONE >> "%s"\n' \
      "$PWD" "$log" "$log" > "$cmd"
    chmod +x "$cmd"
    open -a Terminal "$cmd"
    for _ in $(seq 1 120); do
      grep -q DEVINSTALL-DONE "$log" 2>/dev/null && break
      sleep 10
    done
    cat "$log" 2>/dev/null || echo "no log after 20 minutes"
    ;;
  install)
    xcrun devicectl device install app --device "$DEVICE" "$APP" 2>&1 | tail -6
    ;;
  run)
    # macOS 没有 timeout：用 perl 的 alarm 到点结束
    perl -e 'alarm shift; exec @ARGV' "${2:-40}" \
      xcrun devicectl device process launch --device "$DEVICE" --terminate-existing --console "$BUNDLE" 2>&1
    ;;
  sim)
    seconds=${2:-40}
    shift
    [ $# -gt 0 ] && shift
    wait_free || exit 1
    xcodegen generate --spec project.yml >/dev/null || exit 1
    sim=$(sim_id)
    mkdir -p build
    caffeinate -i xcodebuild -project TailOrbit.xcodeproj -scheme TailOrbit -configuration Debug \
      -destination "platform=iOS Simulator,id=$sim" -derivedDataPath build/sim build > build/last-simbuild.log 2>&1
    status=$?
    grep -E 'error:|warning:|BUILD (SUCCEEDED|FAILED)' build/last-simbuild.log | sort -u | head -60
    [ $status -eq 0 ] || exit $status
    xcrun simctl boot "$sim" 2>/dev/null
    xcrun simctl bootstatus "$sim" -b >/dev/null
    xcrun simctl install "$sim" build/sim/Build/Products/Debug-iphonesimulator/TailOrbit.app
    xcrun simctl privacy "$sim" grant location-always "$BUNDLE"
    perl -e 'alarm shift; exec @ARGV' "$seconds" \
      xcrun simctl launch --console-pty --terminate-running-process "$sim" "$BUNDLE" "$@" 2>&1
    ;;
  *)
    echo "usage: ./mac.sh build|devinstall|install|run [sec]|sim [sec] [app arguments]"
    exit 2
    ;;
esac
