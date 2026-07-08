# Builds the mod and refreshes the package/ folder (what gets published to Thunderstore)
# plus the local Thunderstore Mod Manager profile if present.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet build RevivalSync.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force "$PSScriptRoot\package" | Out-Null
Copy-Item "$PSScriptRoot\bin\Release\RevivalSync.dll" "$PSScriptRoot\package\RevivalSync.dll" -Force
Copy-Item "$PSScriptRoot\CHANGELOG.md" "$PSScriptRoot\package\CHANGELOG.md" -Force
Write-Output "package/ refreshed"

$profilePlugins = Join-Path $env:APPDATA "Thunderstore Mod Manager\DataFolder\REPO\profiles\vanila\BepInEx\plugins\Revival-RevivalSync"
if (Test-Path $profilePlugins) {
    Copy-Item "$PSScriptRoot\bin\Release\RevivalSync.dll","$PSScriptRoot\README.md","$PSScriptRoot\CHANGELOG.md","$PSScriptRoot\manifest.json","$PSScriptRoot\icon.png" $profilePlugins -Force
    Write-Output "local profile updated: $profilePlugins"
}
