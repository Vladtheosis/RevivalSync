# One-time GitHub setup for RevivalSync.
# Run AFTER `gh auth login` has succeeded.
#
# This script deliberately contains NO token — it prompts you for it and hands it
# straight to GitHub's encrypted secret store. Never put the token in any file.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

gh auth status
if ($LASTEXITCODE -ne 0) { throw "Not logged in. Run: gh auth login" }

# private repo so nobody can copy the mod; invite helpers as collaborators
gh repo create RevivalSync --private --source . --push
if ($LASTEXITCODE -ne 0) { throw "repo create/push failed (does the repo already exist?)" }

Write-Host ""
Write-Host "Paste your Thunderstore service-account token (input hidden), then press Enter:"
$secure = Read-Host -AsSecureString
$token = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
$token | gh secret set THUNDERSTORE_TOKEN
$token = $null

Write-Host ""
Write-Host "Done! To publish a version to Thunderstore:"
Write-Host "  .\update-package.ps1      (build + refresh package/)"
Write-Host "  git add -A; git commit -m 'release'"
Write-Host "  git tag 1.0.2; git push; git push --tags"
Write-Host ""
Write-Host "To invite a helper: repo page > Settings > Collaborators > Add people"
Write-Host "  (or: gh api -X PUT repos/USER/RevivalSync/collaborators/THEIR_NAME)"
