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

# build the zip for MANUAL upload on thunderstore.io (Develop > Upload Package)
$version = (Get-Content "$PSScriptRoot\manifest.json" -Raw | ConvertFrom-Json).version_number
$staging = "$PSScriptRoot\bin\zip-staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item "$PSScriptRoot\bin\Release\RevivalSync.dll","$PSScriptRoot\README.md","$PSScriptRoot\CHANGELOG.md","$PSScriptRoot\manifest.json","$PSScriptRoot\icon.png" $staging -Force
$zip = "$PSScriptRoot\bin\Revival-RevivalSync-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$staging\*" -DestinationPath $zip
Remove-Item $staging -Recurse -Force
Write-Output "upload-ready zip: $zip"
