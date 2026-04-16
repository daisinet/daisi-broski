<#
.SYNOPSIS
    One-command installer for daisi-broski-skim on Windows.

.EXAMPLE
    iwr -useb https://raw.githubusercontent.com/daisinet/daisi-broski/main/npm/install.ps1 | iex

.NOTES
    Requires Node.js (which ships with npm). Prints a friendly error and
    a download URL if npm is missing.
#>

$ErrorActionPreference = 'Stop'
$pkg = '@daisinet/broski'

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Host "npm not found." -ForegroundColor Red
    Write-Host "  Install Node.js (includes npm) from https://nodejs.org/"
    Write-Host "  or via winget:  winget install OpenJS.NodeJS.LTS"
    exit 1
}

Write-Host "Installing $pkg via npm..." -ForegroundColor Cyan
npm install -g $pkg
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Installed. Try:" -ForegroundColor Green
Write-Host "  broski https://en.wikipedia.org/wiki/JavaScript --format md"
