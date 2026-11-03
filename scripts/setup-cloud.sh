#!/usr/bin/env bash
set -euo pipefail
cd /workspace/codex
export DOTNET_ROOT=/workspace/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/workspace/.dotnet-home
export NUGET_PACKAGES=/workspace/.nuget/packages
export DOTNET_CLI_TELEMETRY_OPTOUT=1
sdk_version=10.0.403
sdk_sha512=b7b3d8461cf98b07d2552ce9199702fa3ad010ac2671a5f8cd5136f7534e50f6855ba7bbb02b11494563f1d04156276a31cda1103b900e5eecd0d9473dc7b9c5
if [[ ! -x "$DOTNET_ROOT/dotnet" ]] || [[ "$(dotnet --version)" != "$sdk_version" ]]; then
    mkdir -p /workspace/.cache/dameter "$DOTNET_ROOT"
    sdk_archive="/workspace/.cache/dameter/dotnet-sdk-$sdk_version-linux-x64.tar.gz"
    if [[ ! -f "$sdk_archive" ]]; then
        curl --fail --show-error --location "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk_version/dotnet-sdk-$sdk_version-linux-x64.tar.gz" --output "$sdk_archive"
    fi
    printf '%s  %s\n' "$sdk_sha512" "$sdk_archive" | sha512sum --check --status
    tar -xzf "$sdk_archive" -C "$DOTNET_ROOT"
fi
dotnet restore DAMeter.slnx --locked-mode
dotnet build DAMeter.slnx --configuration Release --no-restore --warnaserror -m:2
dotnet test tests/DAMeter.Core.Tests/DAMeter.Core.Tests.csproj --configuration Release --no-build
dotnet test tests/DAMeter.Mac.Tests/DAMeter.Mac.Tests.csproj --configuration Release --no-build
