$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet publish src/DAMeter/DAMeter.csproj --configuration Release --runtime win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:RestoreLockedMode=true' --output artifacts/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    Copy-Item README.md, THIRD-PARTY-NOTICES.txt artifacts/win-x64/
    Copy-Item licenses/* artifacts/win-x64/
    Copy-Item docs/TESTING.md artifacts/win-x64/TESTING.md
    $hash = (Get-FileHash artifacts/win-x64/DAMeter.exe -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content artifacts/win-x64/SHA256SUMS.txt "$hash  DAMeter.exe" -Encoding ascii
    Compress-Archive -Path artifacts/win-x64/* -DestinationPath artifacts/DAMeter-win-x64.zip -Force
}
finally { Pop-Location }
