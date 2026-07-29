<#
    Замер холодного старта (задача #31): запускает собранный плеер несколько раз
    подряд и печатает медиану по этапам запуска.

    Приложение само расставляет отметки времени от создания процесса
    (см. src/ComparisonPlayer/StartupTrace.cs) и пишет их в файл, когда задана
    переменная окружения CVP_STARTUP_TRACE. Скрипт ждёт строку очередного запуска,
    после чего закрывает процесс и запускает следующий.

    Запуск (Windows PowerShell 5.1 или pwsh):
        powershell -ExecutionPolicy Bypass -File tools/measure-startup.ps1 -Exe publish\win-x64\ComparisonPlayer.exe

    Полезные ключи:
        -Runs <n>        сколько запусков (по умолчанию 7; первый отбрасывается как прогрев)
        -KeepWarmup      учитывать и первый запуск
        -Label <текст>   подпись серии в выводе — ею сравнивают «до» и «после»
        -Csv <путь>      дописать результат серии в CSV
#>
[CmdletBinding()]
param(
    [string] $Exe   = '',
    [int]    $Runs  = 7,
    [string] $Label = '',
    [string] $Csv   = '',
    [switch] $KeepWarmup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $PSCommandPath) '..')
if (-not $Exe) { $Exe = Join-Path $root 'publish\win-x64\ComparisonPlayer.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "не найден exe: $Exe" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if (-not $Label) { $Label = Split-Path -Parent $Exe | Split-Path -Leaf }

# Файл отчёта — свой на серию: чужие строки в медиану попасть не должны.
$trace = Join-Path ([System.IO.Path]::GetTempPath()) ("cvp-startup-{0}.log" -f ([guid]::NewGuid().ToString('N')))

Write-Host ("серия «{0}»: {1} запусков" -f $Label, $Runs) -ForegroundColor Cyan
Write-Host ("  exe:   {0}" -f $Exe) -ForegroundColor DarkGray
Write-Host ("  отчёт: {0}" -f $trace) -ForegroundColor DarkGray

$rows = @()
for ($i = 1; $i -le $Runs; $i++) {
    $before = if (Test-Path -LiteralPath $trace) { @(Get-Content -LiteralPath $trace).Count } else { 0 }

    $env:CVP_STARTUP_TRACE = $trace
    $proc = Start-Process -FilePath $Exe -PassThru
    Remove-Item Env:\CVP_STARTUP_TRACE

    # Ждём строку этого запуска: она пишется, когда окно отрисовало первый кадр.
    $deadline = (Get-Date).AddSeconds(60)
    $line = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $trace) {
            $lines = @(Get-Content -LiteralPath $trace)
            if ($lines.Count -gt $before) { $line = $lines[-1]; break }
        }
        if ($proc.HasExited) { throw "процесс завершился, не дойдя до готового окна (запуск $i)" }
        Start-Sleep -Milliseconds 50
    }
    if (-not $line) { throw "запуск $i не отчитался за 60 с" }

    # Плеер убиваем, а не закрываем: обычное закрытие переписало бы сессию
    # и настройки, и следующий запуск мерил бы уже другое состояние.
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { }
    $proc.WaitForExit(10000) | Out-Null

    # Строка вида «HH:mm:ss.fff pid=1234 entry=120.5 settings=130.1 ...»
    $marks = [ordered] @{}
    foreach ($token in ($line -split '\s+')) {
        if ($token -match '^([a-z\-]+)=([0-9.]+)$' -and $Matches[1] -ne 'pid') { $marks[$Matches[1]] = [double] $Matches[2] }
    }
    $rows += ,$marks
    Write-Host ("  запуск {0}: окно готово за {1} мс" -f $i, [math]::Round($marks['shown'], 0)) -ForegroundColor DarkGray

    Start-Sleep -Milliseconds 500
}

if (-not $KeepWarmup -and $rows.Count -gt 1) { $rows = $rows[1..($rows.Count - 1)] }

function Get-Median([double[]] $values) {
    $sorted = @($values | Sort-Object)
    $n = $sorted.Count
    if ($n -eq 0) { return 0 }
    if ($n % 2 -eq 1) { return $sorted[($n - 1) / 2] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

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
Write-Host ("по {0} запускам, мс от создания процесса:" -f $rows.Count) -ForegroundColor Green
Write-Host ("  {0,-12} {1,>8} {2,>8}   {3}" -f 'этап', 'медиана', 'лучший', 'вклад (медиана)')
$prev = 0.0
foreach ($stage in $stages) {
    $value = $median[$stage]
    Write-Host ("  {0,-12} {1,8:N0} {2,8:N0}   +{3:N0}" -f $stage, $value, $best[$stage], ($value - $prev))
    $prev = $value
}

if ($Csv) {
    $record = [ordered] @{ label = $Label; runs = $rows.Count; stamp = (Get-Date).ToString('s') }
    foreach ($stage in $stages) { $record[$stage] = [math]::Round($median[$stage], 1) }
    foreach ($stage in $stages) { $record["$stage-min"] = [math]::Round($best[$stage], 1) }
    [pscustomobject] $record | Export-Csv -LiteralPath $Csv -NoTypeInformation -Append -Encoding UTF8
    Write-Host ("дописано в {0}" -f $Csv) -ForegroundColor DarkGray
}

Remove-Item -LiteralPath $trace -Force -ErrorAction SilentlyContinue
