# One-time setup: store the Thunderstore token as a GitHub Actions secret so
# tag pushes auto-publish to Thunderstore.
#
# Run this, paste the token when asked. The token is never written to any file —
# it goes straight into GitHub's encrypted secret store.
$ErrorActionPreference = "Stop"
$repo = "Vladtheosis/RevivalSync"

# make sure the GitHub CLI is available (installed via winget)
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")
gh --version | Out-Null

# sign in if needed (opens a browser window)
gh auth status 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Signing you into GitHub - a browser window will open..."
    gh auth login --hostname github.com --git-protocol https --web
    if ($LASTEXITCODE -ne 0) { throw "GitHub login failed" }
}

Write-Host ""
Write-Host "Paste your Thunderstore service-account token (input is hidden), then press Enter:"
$secure = Read-Host -AsSecureString
$token = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
if ([string]::IsNullOrWhiteSpace($token)) { throw "No token entered." }

$token | gh secret set TCLI_AUTH_TOKEN --repo $repo
$token = $null
if ($LASTEXITCODE -ne 0) { throw "Failed to set the secret." }

Write-Host ""
Write-Host "Done! TCLI_AUTH_TOKEN is stored (encrypted) in $repo."
Write-Host "Publishing a release is now:"
Write-Host "    .\update-package.ps1"
Write-Host "    git add -A ; git commit -m 'release'"
Write-Host "    git tag 1.0.4 ; git push ; git push --tags"
