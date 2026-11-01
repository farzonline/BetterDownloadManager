# Builds the release: runs every test suite, publishes the apps, compiles the installer.
# Exits non-zero (and builds nothing) if any test fails.
param([switch]$SkipTests, [switch]$SkipE2E)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Run($what, [scriptblock]$cmd) {
    Write-Host "==> $what" -ForegroundColor Green
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

if (-not $SkipTests) {
    Run 'dotnet tests' { dotnet test --nologo -v q }
    Run 'extension tests' { node --test extension/test/ }
    if (-not $SkipE2E -and (Test-Path tests/e2e/node_modules)) {
        Run 'browser acceptance tests' { Push-Location tests/e2e; try { npm test } finally { Pop-Location } }
    }
}

$publish = Join-Path $root 'dist/publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($project in 'BDM.App', 'BDM.Host', 'BDM.Cli') {
    Run "publish $project" { dotnet publish "src/$project" -c Release -r win-x64 --self-contained true -o $publish --nologo -v q }
}

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup' }
Run 'compile installer' { & $iscc /Q installer/bdm.iss }

Write-Host "Done: $(Join-Path $root 'dist/BDM-Setup.exe')" -ForegroundColor Green
