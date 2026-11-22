param([Parameter(Mandatory = $true)][int]$RunNumber)
$ErrorActionPreference = 'Stop'
$base = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\VERSION') -Encoding UTF8 -Raw).Trim()
if ($base -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw 'Invalid VERSION' }
if ($RunNumber -lt 1 -or $RunNumber -gt 65534) { throw 'Run number outside assembly version range' }
"$($Matches[1]).$($Matches[2]).$RunNumber"
