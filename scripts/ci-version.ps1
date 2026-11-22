$ErrorActionPreference = 'Stop'
$base = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\VERSION') -Encoding UTF8 -Raw).Trim()
if ($base -notmatch '^\d+\.\d+\.\d$') { throw 'Invalid VERSION (expected MAJOR.MINOR.PATCH, patch 0-9)' }
$base
