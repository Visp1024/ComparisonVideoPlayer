<#
    Выпускает релиз CVP: ставит тег версии на текущий коммит и отправляет его в
    origin. Дальше всё делает CI (.github/workflows/release.yml) — собирает
    переносимый архив и инсталлятор и выкладывает их в релиз GitHub.

    Запуск (Windows PowerShell 5.1 или pwsh) из корня репозитория:
        powershell -ExecutionPolicy Bypass -File tools/release.ps1

    Полезные ключи:
        -Version <x.y.z>  версия релиза; по умолчанию берётся из csproj
        -Branch <имя>     ветка, с которой выпускается релиз (по умолчанию master)
        -Force            переставить тег, если он уже есть локально

    Версию поставки задаёт <Version> в src/ComparisonPlayer/ComparisonPlayer.csproj:
    поднимите её там и закоммитьте, прежде чем выпускать новый релиз.
#>
[CmdletBinding()]
param(
    [string] $Version = '',
    [string] $Branch  = 'master',
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root    = Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $PSCommandPath) '..')
$project = Join-Path $root 'src\ComparisonPlayer\ComparisonPlayer.csproj'

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $GitArgs)
    $out = & git -C $root @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("git {0} → {1}" -f ($GitArgs -join ' '), ($out -join "`n")) }
    return ($out -join "`n").Trim()
}

# 1. Версия: ключ или <Version> из csproj — та же, что уедет в exe при сборке.
if (-not $Version) {
    if (-not (Test-Path -LiteralPath $project)) { throw "не найден проект: $project" }
    $m = [regex]::Match((Get-Content -LiteralPath $project -Raw), '<Version>\s*([^<]+?)\s*</Version>')
    if (-not $m.Success) { throw "в csproj нет <Version> — задайте версию ключом -Version" }
    $Version = $m.Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "версия не похожа на номер поставки: $Version" }
$tag = "v$Version"

# 2. Релиз выпускается только с чистого дерева и с той ветки, что уже в origin:
#    CI соберёт ровно тот коммит, на который встанет тег.
$branchNow = Invoke-Git rev-parse --abbrev-ref HEAD
if ($branchNow -ne $Branch) { throw "релиз выпускается с ветки $Branch, а HEAD на $branchNow" }

$dirty = Invoke-Git status --porcelain
if ($dirty) { throw "в рабочем дереве есть незакоммиченные изменения:`n$dirty" }

Invoke-Git fetch origin --tags --quiet | Out-Null
$local  = Invoke-Git rev-parse HEAD
$remote = Invoke-Git rev-parse "origin/$Branch"
if ($local -ne $remote) {
    throw "ветка $Branch расходится с origin/$Branch — сначала отправьте коммиты (git push)"
}

# 3. Тег. Уже выпущенную версию молча не переписываем: релиз с таким именем есть.
$existing = & git -C $root tag --list $tag
if ($existing) {
    if (-not $Force) { throw "тег $tag уже есть — поднимите <Version> в csproj или запустите с -Force" }
    Invoke-Git tag -d $tag | Out-Null
    Write-Host "старый тег $tag удалён локально (-Force)" -ForegroundColor Yellow
}

Invoke-Git tag -a $tag -m "CVP $Version" | Out-Null

$pushArgs = @('push', 'origin', $tag)
if ($Force) { $pushArgs += '--force' }
Invoke-Git @pushArgs | Out-Null

$url = (Invoke-Git remote get-url origin) -replace '\.git$', ''
Write-Host ''
Write-Host ("Тег {0} отправлен — сборку релиза ведёт CI" -f $tag) -ForegroundColor Green
Write-Host ("  прогон:  {0}/actions/workflows/release.yml" -f $url)
Write-Host ("  релиз:   {0}/releases/tag/{1}" -f $url, $tag)
