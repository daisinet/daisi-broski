<#
.SYNOPSIS
    Build + publish daisi-broski-skim and its four platform packages to npm.

.DESCRIPTION
    Runs `dotnet publish -r <rid> --self-contained -p:PublishSingleFile=true`
    for each supported runtime, copies the resulting binary into its
    platform package directory, stamps the version across all five
    package.json files, then `npm publish`es them — platform packages
    first so the main package's optionalDependencies resolve on install.

.PARAMETER Version
    Semver-compliant version to publish. Required.

.PARAMETER DryRun
    Build + stamp + `npm pack` (not publish). Useful for sanity-checking
    what would be pushed without actually pushing.

.EXAMPLE
    ./publish.ps1 -Version 0.1.0

.EXAMPLE
    ./publish.ps1 -Version 0.1.0 -DryRun

.NOTES
    Requires: dotnet 10+, Node 16+, npm, a logged-in `npm login` session
    with publish rights on `daisi-broski-skim` and its platform packages.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Version,

  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ---- paths (relative to this script's location) ----
$npmRoot   = $PSScriptRoot
$repoRoot  = Split-Path $npmRoot -Parent
$skimmerCs = Join-Path $repoRoot 'src/dotnet/Daisi.Broski.Skimmer/Daisi.Broski.Skimmer.csproj'
$mainPkg   = Join-Path $npmRoot 'broski'
$platformsDir = Join-Path $npmRoot 'platforms'

# ---- platform matrix ----
# rid: the .NET RID passed to `dotnet publish`
# dir: the sub-folder under npm/platforms
# bin: the filename of the binary that ends up in the publish/ folder
$platforms = @(
  @{ rid = 'linux-x64';    dir = 'linux-x64';    bin = 'daisi-broski-skim'     },
  @{ rid = 'osx-x64';      dir = 'darwin-x64';   bin = 'daisi-broski-skim'     },
  @{ rid = 'osx-arm64';    dir = 'darwin-arm64'; bin = 'daisi-broski-skim'     },
  @{ rid = 'win-x64';      dir = 'win32-x64';    bin = 'daisi-broski-skim.exe' }
)

function Update-Version([string]$pkgJsonPath, [string]$ver) {
  # Text-only edit to preserve the file's hand-rolled formatting
  # across releases. Replaces:
  #   "version": "<anything>"
  # and any value-quoted versions in the optionalDependencies block:
  #   "@daisinet/broski-...": "<anything>"
  # with the new version. Avoids PowerShell ConvertTo-Json's
  # verbose reformatting that would churn every line on every
  # publish.
  $raw = Get-Content $pkgJsonPath -Raw
  $raw = [regex]::Replace($raw,
    '("version"\s*:\s*")[^"]*(")',
    "`${1}$ver`${2}", 1)
  # Pin every `@daisinet/broski-*` dep version (the optionalDeps
  # on the main package) to the same value.
  $raw = [regex]::Replace($raw,
    '("@daisinet/broski-[^"]+"\s*:\s*")[^"]*(")',
    "`${1}$ver`${2}")
  Set-Content $pkgJsonPath $raw -NoNewline
}

function Run([string]$what, [scriptblock]$block) {
  Write-Host "-> $what" -ForegroundColor Cyan
  & $block
  if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

# ---- 1. stamp version across all package.json files ----
Write-Host ""
Write-Host "Stamping version $Version across package.json files…" -ForegroundColor Yellow
Update-Version (Join-Path $mainPkg 'package.json') $Version
foreach ($p in $platforms) {
  Update-Version (Join-Path $platformsDir "$($p.dir)/package.json") $Version
}

# ---- 2. build self-contained binaries for each platform ----
foreach ($p in $platforms) {
  $outDir = Join-Path $platformsDir $p.dir
  Write-Host ""
  Write-Host "Building $($p.rid) → $outDir" -ForegroundColor Yellow
  Run "dotnet publish for $($p.rid)" {
    & dotnet publish $skimmerCs `
      -c Release `
      -r $p.rid `
      --self-contained true `
      -p:PublishSingleFile=true `
      -p:IncludeNativeLibrariesForSelfExtract=true `
      -p:DebugType=None -p:DebugSymbols=false `
      -p:Version=$Version `
      -o (Join-Path $outDir 'publish-tmp')
  }
  $srcBin = Join-Path $outDir "publish-tmp/$($p.bin)"
  if (-not (Test-Path $srcBin)) {
    throw "Expected binary not found: $srcBin"
  }
  Copy-Item $srcBin (Join-Path $outDir $p.bin) -Force
  Remove-Item (Join-Path $outDir 'publish-tmp') -Recurse -Force
}

# ---- 3. publish (or pack, in dry-run) ----
#       Platform packages FIRST so the main package's
#       optionalDependencies resolve on install.
$verb = if ($DryRun) { 'pack' } else { 'publish' }
Write-Host ""
Write-Host "npm $verb (platform packages first, then main)…" -ForegroundColor Yellow
foreach ($p in $platforms) {
  $dir = Join-Path $platformsDir $p.dir
  Push-Location $dir
  try {
    Run "npm $verb ($($p.dir))" {
      if ($DryRun) { npm pack --quiet } else { npm publish --access public }
    }
  } finally { Pop-Location }
}
Push-Location $mainPkg
try {
  Run "npm $verb (main)" {
    if ($DryRun) { npm pack --quiet } else { npm publish --access public }
  }
} finally { Pop-Location }

Write-Host ""
Write-Host ("Done. Version: {0}" -f $Version) -ForegroundColor Green
if ($DryRun) {
  Write-Host "  (dry run - .tgz tarballs written next to each package.json)" -ForegroundColor DarkGray
}
