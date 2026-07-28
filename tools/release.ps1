<#
    Выпускает релиз CVP: сам поднимает версию, ставит тег и отправляет его в
    origin. Дальше всё делает CI (.github/workflows/release.yml) — собирает
    переносимый архив и инсталлятор и выкладывает их в релиз GitHub.

    Запуск (Windows PowerShell 5.1 или pwsh) из корня репозитория:
        powershell -ExecutionPolicy Bypass -File tools/release.ps1

    Версию решает скрипт: если версия из <Version> в csproj ещё не выпускалась,
    выпускается она; если тег на неё уже есть — версия поднимается (по умолчанию
    патч) и новый номер коммитится в csproj. Так что кнопку можно просто нажать.

    Полезные ключи:
        -Bump patch|minor|major  какую часть поднимать (по умолчанию patch)
        -Version <x.y.z>         явная версия вместо вычисленной
        -Branch <имя>            ветка релиза (по умолчанию master)
        -Force                   переставить тег, если он уже есть
#>
[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major')]
    [string] $Bump    = 'patch',
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
    # git пишет в stderr обычный ход дела («To https://...» при push, счётчики
    # объектов при fetch). При $ErrorActionPreference = 'Stop' Windows PowerShell
    # 5.1 считает такую строку ошибкой и валит скрипт на успешной команде —
    # поэтому судим только по коду возврата.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try   { $out = & git -C $root @GitArgs 2>&1 }
    finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { throw ("git {0} → {1}" -f ($GitArgs -join ' '), ($out -join "`n")) }
    return ($out -join "`n").Trim()
}

function Step-Version {
    param([string] $Current, [string] $Part)
    $n = $Current.Split('.')
    if ($n.Count -lt 3) { throw "версию $Current не поднять автоматически — задайте её ключом -Version" }
    switch ($Part) {
        'major' { "{0}.0.0"   -f ([int]$n[0] + 1) }
        'minor' { "{0}.{1}.0" -f $n[0], ([int]$n[1] + 1) }
        'patch' { "{0}.{1}.{2}" -f $n[0], $n[1], ([int]$n[2] + 1) }
    }
}

# 1. Релиз выпускается только с чистого дерева и с той ветки, что уже в origin:
#    CI соберёт ровно тот коммит, на который встанет тег.
$branchNow = Invoke-Git rev-parse --abbrev-ref HEAD
if ($branchNow -ne $Branch) { throw "релиз выпускается с ветки $Branch, а HEAD на $branchNow" }

$dirty = Invoke-Git status --porcelain
if ($dirty) { throw "в рабочем дереве есть незакоммиченные изменения:`n$dirty" }

Invoke-Git fetch origin --tags --quiet | Out-Null
if ((Invoke-Git rev-parse HEAD) -ne (Invoke-Git rev-parse "origin/$Branch")) {
    throw "ветка $Branch расходится с origin/$Branch — сначала отправьте коммиты (git push)"
}

# 2. Версия. <Version> в csproj — источник правды: он уезжает в exe, в имя архива
#    и в инсталлятор. Ещё не выпускавшуюся версию releasим как есть (кто-то поднял
#    её руками), уже выпущенную — поднимаем сами.
if (-not (Test-Path -LiteralPath $project)) { throw "не найден проект: $project" }
$csproj = [System.IO.File]::ReadAllText($project)
$m = [regex]::Match($csproj, '(<Version>\s*)([^<]+?)(\s*</Version>)')
if (-not $m.Success) { throw "в csproj нет <Version> — задайте версию ключом -Version" }
$current = $m.Groups[2].Value

if ($Version) {
    $target = $Version
}
elseif (& git -C $root tag --list "v$current") {
    $target = Step-Version -Current $current -Part $Bump
    Write-Host ("версия $current уже выпущена — поднимаю $Bump до $target") -ForegroundColor DarkGray
}
else {
    $target = $current
    Write-Host ("версия $target из csproj ещё не выпускалась — выпускаю её") -ForegroundColor DarkGray
}
if ($target -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "версия не похожа на номер поставки: $target" }
$tag = "v$target"

# 3. Тег. Уже выпущенную версию молча не переписываем: релиз с таким именем есть.
if (& git -C $root tag --list $tag) {
    if (-not $Force) { throw "тег $tag уже есть — задайте другую версию ключом -Version или запустите с -Force" }
    Invoke-Git tag -d $tag | Out-Null
    Write-Host "старый тег $tag удалён локально (-Force)" -ForegroundColor Yellow
}

# 4. Новый номер уезжает в csproj отдельным коммитом — тег встаёт уже на него,
#    поэтому собранный exe и имя файла поставки совпадут с именем релиза.
if ($target -ne $current) {
    $updated = $csproj.Remove($m.Index, $m.Length).Insert($m.Index, ($m.Groups[1].Value + $target + $m.Groups[3].Value))
    # Кодировку файла не трогаем: перезаписываем ровно тем, чем он был (с BOM или без).
    $bytes = [System.IO.File]::ReadAllBytes($project)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    [System.IO.File]::WriteAllText($project, $updated, (New-Object System.Text.UTF8Encoding($hasBom)))

    Invoke-Git add -- 'src/ComparisonPlayer/ComparisonPlayer.csproj' | Out-Null
    Invoke-Git commit -m "Версия поставки $target" | Out-Null
    Invoke-Git push origin $Branch | Out-Null
    Write-Host ("csproj: $current -> $target, коммит отправлен в origin/$Branch") -ForegroundColor DarkGray
}

Invoke-Git tag -a $tag -m "CVP $target" | Out-Null

$pushArgs = @('push', 'origin', $tag)
if ($Force) { $pushArgs += '--force' }
Invoke-Git @pushArgs | Out-Null

$url = (Invoke-Git remote get-url origin) -replace '\.git$', ''
Write-Host ''
Write-Host ("Тег {0} отправлен — сборку релиза ведёт CI" -f $tag) -ForegroundColor Green
Write-Host ("  прогон:  {0}/actions/workflows/release.yml" -f $url)
Write-Host ("  релиз:   {0}/releases/tag/{1}" -f $url, $tag)
