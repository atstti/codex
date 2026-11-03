#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ "$(uname -s)" != Darwin ]]; then
    echo 'publish-macos.sh requires macOS, the .NET 10 SDK, and Xcode command line tools.' >&2
    exit 1
fi

rid="${1:-}"
if [[ -z "$rid" ]]; then
    case "$(uname -m)" in
        arm64) rid=osx-arm64 ;;
        x86_64) rid=osx-x64 ;;
        *) echo 'Unsupported Mac architecture.' >&2; exit 1 ;;
    esac
fi
case "$rid" in
    osx-arm64) swift_target=arm64-apple-macos15.0 ;;
    osx-x64) swift_target=x86_64-apple-macos15.0 ;;
    *) echo 'Usage: scripts/publish-macos.sh [osx-arm64|osx-x64]' >&2; exit 1 ;;
esac

for tool in dotnet xcrun codesign ditto hdiutil python3; do
    command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done

root="$PWD"
output="$root/artifacts/macos-$rid"
app="$output/DAMeter.app"
macos="$app/Contents/MacOS"
resources="$app/Contents/Resources"
# Delete only this script's own generated .app; unrelated artifacts are retained.
rm -rf "$app"
mkdir -p "$macos" "$resources" "$output"
temporary="$(mktemp -d "${TMPDIR:-/tmp}/dameter-publish.XXXXXX")"
trap 'rm -rf "$temporary"' EXIT

# Bundling .NET's dylibs beside the apphost preserves their runtime lookup paths.
# Ordinary Mac publish has a separate lock graph for each RID.
dotnet publish src/DAMeter.Mac/DAMeter.Mac.csproj \
    --configuration Release --runtime "$rid" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -p:DebugType=None -p:DebugSymbols=false -p:RestoreLockedMode=true \
    --output "$macos"

xcrun swiftc -parse-as-library -swift-version 5 -O \
    -target "$swift_target" \
    -framework ScreenCaptureKit -framework AVFoundation \
    -framework CoreMedia -framework CoreAudio -framework AudioToolbox -framework CoreGraphics -framework AppKit \
    native/DAMeter.AudioCapture/*.swift \
    -o "$macos/DAMeter.AudioCapture"
chmod 755 "$macos/DAMeter.Mac" "$macos/DAMeter.AudioCapture"

cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>CFBundleName</key><string>DAMeter</string>
    <key>CFBundleDisplayName</key><string>DAMeter</string>
    <key>CFBundleIdentifier</key><string>org.atstti.DAMeter</string>
    <key>CFBundleExecutable</key><string>DAMeter.Mac</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>0.1.0</string>
    <key>CFBundleShortVersionString</key><string>0.1.0</string>
    <key>CFBundleIconFile</key><string>DAMeter.icns</string>
    <key>LSMinimumSystemVersion</key><string>15.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSScreenCaptureUsageDescription</key><string>システム音声のレベルを表示するために使用します。画面・音声の録画、保存、送信は行いません。</string>
    <key>NSAudioCaptureUsageDescription</key><string>再生中のシステム音声をメーターで表示するために使用します。マイクは使用しません。</string>
</dict></plist>
PLIST
/usr/bin/plutil -lint "$app/Contents/Info.plist"

# A native, locally rendered icon avoids external build-time image dependencies.
cat > "$temporary/CreateIcon.swift" <<'SWIFT'
import AppKit
let target = CommandLine.arguments[1]
let image = NSImage(size: NSSize(width: 1024, height: 1024))
image.lockFocus()
NSColor(calibratedRed: 0.055, green: 0.082, blue: 0.12, alpha: 1).setFill()
NSBezierPath(roundedRect: NSRect(x: 32, y: 32, width: 960, height: 960), xRadius: 210, yRadius: 210).fill()
let heights: [CGFloat] = [230, 390, 560, 430, 660, 510, 330]
for (index, height) in heights.enumerated() {
    let color = index < 4 ? NSColor(calibratedRed: 0.24, green: 0.85, blue: 0.59, alpha: 1)
        : index < 6 ? NSColor(calibratedRed: 0.98, green: 0.78, blue: 0.28, alpha: 1)
        : NSColor(calibratedRed: 1, green: 0.32, blue: 0.36, alpha: 1)
    color.setFill()
    NSBezierPath(roundedRect: NSRect(x: 164 + CGFloat(index) * 102, y: 180, width: 66, height: height), xRadius: 20, yRadius: 20).fill()
}
image.unlockFocus()
guard let tiff = image.tiffRepresentation,
      let bitmap = NSBitmapImageRep(data: tiff),
      let png = bitmap.representation(using: .png, properties: [:]) else {
    fatalError("Could not render app icon")
}
try png.write(to: URL(fileURLWithPath: target))
SWIFT
xcrun swift "$temporary/CreateIcon.swift" "$temporary/master.png"
iconset="$temporary/DAMeter.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    /usr/bin/sips -z "$size" "$size" "$temporary/master.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    doubled=$((size * 2))
    /usr/bin/sips -z "$doubled" "$doubled" "$temporary/master.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
/usr/bin/iconutil --convert icns "$iconset" --output "$resources/DAMeter.icns"

mkdir -p "$resources/licenses"
cp licenses/*.txt "$resources/licenses/"
cp THIRD-PARTY-NOTICES.txt "$resources/"
cp docs/MACOS.md "$resources/README-ja.md"
cp docs/TESTING.md "$resources/TESTING.md"

# This is a local ad-hoc signature, not a Developer ID or notarization. The JIT
# entitlement is required by the bundled .NET runtime on Apple Silicon.
cat > "$temporary/entitlements.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>com.apple.security.cs.allow-jit</key><true/>
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
</dict></plist>
PLIST
while IFS= read -r -d '' binary; do
    case "$(/usr/bin/file -b "$binary")" in
        *Mach-O*) /usr/bin/codesign --force --sign - --timestamp=none --entitlements "$temporary/entitlements.plist" "$binary" ;;
    esac
done < <(find "$macos" -type f -print0)
/usr/bin/codesign --force --sign - --timestamp=none --entitlements "$temporary/entitlements.plist" "$app"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$app"

# --check never starts capture or requests Screen Recording permission.
case "$rid:$(uname -m)" in
    osx-arm64:arm64|osx-x64:x86_64)
        "$macos/DAMeter.AudioCapture" --check
        "$macos/DAMeter.AudioCapture" --self-test
        # Exercise Avalonia, its native libraries, and real window creation. The
        # application skips audio/tray/persistence and closes itself in this mode.
        python3 - "$macos/DAMeter.Mac" <<'PY'
import subprocess, sys
subprocess.run([sys.argv[1], '--smoke-test'], check=True, timeout=45)
PY
        ;;
    *) echo 'Cross-built app: execution checks skipped for the other CPU architecture.' ;;
esac

commit="$(git rev-parse HEAD 2>/dev/null || printf 'unknown')"
printf 'DAMeter %s\nSource: %s\n.NET SDK: %s\nSignature: ad-hoc, not notarized\nSystem audio / interactive UI acceptance: not performed by this script\n' \
    "$rid" "$commit" "$(dotnet --version)" > "$output/BUILD-INFO.txt"
cp docs/MACOS.md "$output/README-ja.md"
cp THIRD-PARTY-NOTICES.txt "$output/"

/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$app" "$output/DAMeter-$rid.zip"
mkdir -p "$temporary/dmg"
/usr/bin/ditto "$app" "$temporary/dmg/DAMeter.app"
ln -s /Applications "$temporary/dmg/Applications"
/usr/bin/hdiutil create -volname DAMeter -srcfolder "$temporary/dmg" -ov -format UDZO "$output/DAMeter-$rid.dmg"
(
    cd "$output"
    /usr/bin/shasum -a 256 "DAMeter-$rid.zip" "DAMeter-$rid.dmg" > SHA256SUMS.txt
)
echo "Mac distribution created at $output"
