<#
    Замер холодного старта (задача #31): запускает собранный плеер несколько раз
    подряд и печатает время этапов запуска.

    Приложение само расставляет отметки времени от создания процесса
    (см. src/ComparisonPlayer/StartupTrace.cs) и пишет их в файл, когда задана
    переменная окружения CVP_STARTUP_TRACE. Скрипт ждёт строку очередного запуска,
    после чего закрывает процесс и запускает следующий.

    Сборок можно передать несколько: тогда они чередуются внутри одной серии.
    Так и надо сравнивать «до» и «после» — две серии подряд ловят разную фоновую
    нагрузку машины, и разница между ними наполовину состоит из неё.

    Запуск (Windows PowerShell 5.1 или pwsh):
        powershell -ExecutionPolicy Bypass -File tools/measure-startup.ps1 -Exe publish\win-x64\ComparisonPlayer.exe
        powershell -ExecutionPolicy Bypass -File tools/measure-startup.ps1 -Exe publish\base\CVP.exe,publish\new\CVP.exe

    Полезные ключи:
        -Runs <n>        сколько запусков каждой сборки (по умолчанию 7)
        -KeepWarmup      учитывать и первый запуск каждой сборки (иначе он прогревочный)
        -Csv <путь>      дописать медианы в CSV
#>
[CmdletBinding()]
param(
    [string[]] $Exe  = @(),
    [int]      $Runs = 7,
    [string]   $Csv  = '',
    [switch]   $KeepWarmup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $PSCommandPath) '..')
if (-not $Exe -or $Exe.Count -eq 0) { $Exe = @(Join-Path $root 'publish\win-x64\ComparisonPlayer.exe') }

$builds = @()
foreach ($path in $Exe) {
    if (-not (Test-Path -LiteralPath $path)) { throw "не найден exe: $path" }
    $full = (Resolve-Path -LiteralPath $path).Path
    $builds += ,[pscustomobject] @{
        Path  = $full
        Label = Split-Path (Split-Path -Parent $full) -Leaf
        Rows  = @()
    }
}

# Файл отчёта — свой на серию: чужие строки в медиану попасть не должны.
$trace = Join-Path ([System.IO.Path]::GetTempPath()) ("cvp-startup-{0}.log" -f ([guid]::NewGuid().ToString('N')))

Write-Host ("серия: {0} сборок × {1} запусков, вперемешку" -f $builds.Count, $Runs) -ForegroundColor Cyan
foreach ($b in $builds) { Write-Host ("  {0,-14} {1}" -f $b.Label, $b.Path) -ForegroundColor DarkGray }

function Invoke-Run([string] $exePath, [string] $tracePath) {
    $before = if (Test-Path -LiteralPath $tracePath) { @(Get-Content -LiteralPath $tracePath).Count } else { 0 }

    $env:CVP_STARTUP_TRACE = $tracePath
    $proc = Start-Process -FilePath $exePath -PassThru
    Remove-Item Env:\CVP_STARTUP_TRACE

    # Ждём строку этого запуска: она пишется, когда окно отрисовало первый кадр.
    $deadline = (Get-Date).AddSeconds(60)
    $line = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $tracePath) {
            $lines = @(Get-Content -LiteralPath $tracePath)
            if ($lines.Count -gt $before) { $line = $lines[-1]; break }
        }
        if ($proc.HasExited) { throw "процесс завершился, не дойдя до готового окна: $exePath" }
        Start-Sleep -Milliseconds 50
    }
    if (-not $line) { throw "запуск не отчитался за 60 с: $exePath" }

    # Плеер убиваем, а не закрываем: обычное закрытие переписало бы сессию
    # и настройки, и следующий запуск мерил бы уже другое состояние.
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
    $proc.WaitForExit(10000) | Out-Null
    Start-Sleep -Milliseconds 400

    # Строка вида «HH:mm:ss.fff pid=1234 entry=120.5 settings=130.1 ...»
    $marks = [ordered] @{}
    foreach ($token in ($line -split '\s+')) {
        if ($token -match '^([a-z\-]+)=([0-9.]+)$' -and $Matches[1] -ne 'pid') { $marks[$Matches[1]] = [double] $Matches[2] }
    }
    return $marks
}

for ($i = 1; $i -le $Runs; $i++) {
    foreach ($build in $builds) {
        $marks = Invoke-Run $build.Path $trace
        $build.Rows += ,$marks
        Write-Host ("  запуск {0} · {1,-14} окно готово за {2} мс" -f $i, $build.Label, [math]::Round($marks['shown'], 0)) -ForegroundColor DarkGray
    }
}

function Get-Median([double[]] $values) {
    $sorted = @($values | Sort-Object)
    $n = $sorted.Count
    if ($n -eq 0) { return 0 }
    if ($n % 2 -eq 1) { return $sorted[($n - 1) / 2] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

foreach ($build in $builds) {
    $rows = $build.Rows
    if (-not $KeepWarmup -and $rows.Count -gt 1) { $rows = $rows[1..($rows.Count - 1)] }

    $stages = @($rows[0].Keys)
    $median = [ordered] @{}
    $best   = [ordered] @{}
    foreach ($stage in $stages) {
        $values = @($rows | ForEach-Object { $_[$stage] })
        $median[$stage] = Get-Median $values
        $best[$stage]   = ($values | Measure-Object -Minimum).Minimum
    }

    # Минимум показываем рядом с медианой: фоновая нагрузка машины умеет удвоить
    # отдельный запуск, и по одной медиане правку от шума не отличить.
    Write-Host ''
    Write-Host ("{0}: {1} запусков, мс от создания процесса" -f $build.Label, $rows.Count) -ForegroundColor Green
    Write-Host ("  {0,-12} {1,8} {2,8}   {3}" -f 'этап', 'медиана', 'лучший', 'вклад')
    $prev = 0.0
    foreach ($stage in $stages) {
        $value = $median[$stage]
        Write-Host ("  {0,-12} {1,8:N0} {2,8:N0}   +{3:N0}" -f $stage, $value, $best[$stage], ($value - $prev))
        $prev = $value
    }

    if ($Csv) {
        $record = [ordered] @{ label = $build.Label; runs = $rows.Count; stamp = (Get-Date).ToString('s') }
        foreach ($stage in $stages) { $record[$stage] = [math]::Round($median[$stage], 1) }
        foreach ($stage in $stages) { $record["$stage-min"] = [math]::Round($best[$stage], 1) }
        [pscustomobject] $record | Export-Csv -LiteralPath $Csv -NoTypeInformation -Append -Encoding UTF8
    }
}

if ($Csv) { Write-Host ("`nдописано в {0}" -f $Csv) -ForegroundColor DarkGray }
Remove-Item -LiteralPath $trace -Force -ErrorAction SilentlyContinue
