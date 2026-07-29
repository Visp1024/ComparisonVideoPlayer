<#
    Собирает публикационный билд ComparisonPlayer: один self-contained exe плюс
    нативные библиотеки FFmpeg рядом с ним (приложение ищет их в подкаталоге FFmpeg,
    см. src/ComparisonPlayer/AppEnv.cs). Результат — папка publish/<runtime>,
    которую можно скопировать на другую машину без установленного .NET.

    Запуск (Windows PowerShell 5.1 или pwsh):
        powershell -ExecutionPolicy Bypass -File tools/publish.ps1

    Полезные ключи:
        -FrameworkDependent   без рантайма .NET внутри (нужен установленный .NET 9 Desktop)
        -NoSingleFile         обычная папка со сборками вместо одного exe
        -NoFFmpeg             не копировать FFmpeg в билд
        -Compress             сжать содержимое single-file exe (втрое меньше, но старт медленнее)
        -FFmpegDir <путь>     явный каталог с avcodec-*.dll и ffmpeg.exe
        -OutDir <путь>        куда класть результат
        -Version <x.y.z>      версия поставки вместо указанной в csproj (её ставит CI из тега)
        -Zip                  дополнительно упаковать результат в переносимый архив

    Инсталлятор собирается отдельно: tools/make-installer.ps1 (Inno Setup).
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [string] $OutDir        = '',
    [string] $FFmpegDir     = '',
    [string] $Version       = '',
    [switch] $FrameworkDependent,
    [switch] $NoSingleFile,
    [switch] $NoFFmpeg,
    [switch] $Compress,
    [switch] $Zip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root    = Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $PSCommandPath) '..')
$project = Join-Path $root 'src\ComparisonPlayer\ComparisonPlayer.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw "не найден проект: $project" }

if (-not $OutDir) { $OutDir = Join-Path $root "publish\$Runtime" }

# Каталог FFmpeg: явный ключ -> переменные окружения приложения -> tools\ffmpeg\bin в
# репозитории (в git не хранится, распаковывается из ffmpeg-n7.1-*-win64-gpl-shared).
if (-not $NoFFmpeg -and -not $FFmpegDir) {
    # @(...) вокруг результата обязательно: один найденный путь иначе станет строкой,
    # и [0] вернёт первый символ вместо каталога.
    $candidates = @(@(
        $env:COMPARISONPLAYER_FFMPEG_DIR,
        $env:SPIKE_FFMPEG_DIR,
        (Join-Path $root 'tools\ffmpeg\bin'),
        'C:\ffmpeg\bin'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($candidates.Count -gt 0) { $FFmpegDir = $candidates[0] }
}

# Пересборка с нуля: остатки прошлого билда не должны уезжать в поставку.
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }

$selfContained = -not $FrameworkDependent
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    $(if ($selfContained) { '--self-contained' } else { '--no-self-contained' }),
    '-o', $OutDir,
    '--nologo'
)
# Версия попадает в exe, а из неё — в имя архива и в инсталлятор.
if ($Version) { $publishArgs += "-p:Version=$Version" }
if (-not $NoSingleFile) {
    $publishArgs += @(
        '-p:PublishSingleFile=true',
        # Нативные библиотеки (D3D11-шейдеры и биндинги FFmpeg у FlyleafLib)
        # обязаны распаковываться на диск: из памяти они не грузятся.
        '-p:IncludeNativeLibrariesForSelfExtract=true'
    )
    # Сжатие содержимого exe по умолчанию выключено (задача #31): распаковка идёт
    # при каждом запуске и стоит около 130 мс из ~1,4 с холодного старта, а с
    # предкомпилированным кодом (ReadyToRun) — вдвое больше. Ключ оставлен для
    # случая, когда важнее размер файла.
    if ($selfContained -and $Compress) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }
}

Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

# FFmpeg кладём в подкаталог FFmpeg — там его ищет AppEnv, если не задана переменная
# окружения. ffmpeg.exe нужен для сборки кэша кадров (фаза 4), библиотеки — для декода.
if ($NoFFmpeg) {
    Write-Host 'FFmpeg не копируется (-NoFFmpeg)' -ForegroundColor Yellow
}
elseif (-not $FFmpegDir) {
    Write-Warning 'каталог FFmpeg не найден — билд без него откроет файл только при заданной COMPARISONPLAYER_FFMPEG_DIR'
}
else {
    $ffOut = Join-Path $OutDir 'FFmpeg'
    New-Item -ItemType Directory -Path $ffOut -Force | Out-Null
    $copied = 0
    foreach ($item in Get-ChildItem -LiteralPath $FFmpegDir -File) {
        if ($item.Extension -notin @('.dll', '.exe')) { continue }
        # ffplay в поставке ни к чему — он тянет SDL и никем не вызывается.
        if ($item.Name -eq 'ffplay.exe') { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $ffOut -Force
        $copied++
    }
    if ($copied -eq 0) { throw "в каталоге FFmpeg нет ни одной библиотеки: $FFmpegDir" }
    Write-Host "FFmpeg: скопировано $copied файлов из $FFmpegDir" -ForegroundColor DarkGray
}

$exe = Join-Path $OutDir 'ComparisonPlayer.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "publish отработал, но exe не найден: $exe" }

$sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $OutDir -Recurse -File |
    Measure-Object -Property Length -Sum).Sum / 1MB), 1)
$mode = if ($selfContained) { 'self-contained' } else { 'framework-dependent' }
if (-not $NoSingleFile) { $mode += ', single-file' }
if ($selfContained -and $Compress) { $mode += ', сжатый' }

# Переносимый вариант поставки: распаковал куда угодно и запустил, без установки.
$zipPath = ''
if ($Zip) {
    $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    if (-not $version) { $version = '1.0.0' }
    $version = ($version -split '\+')[0]

    $zipPath = Join-Path (Split-Path -Parent $OutDir) ("CVP-{0}-{1}.zip" -f $version, $Runtime)
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zipPath
    Write-Host ("архив: {0}" -f $zipPath) -ForegroundColor DarkGray
}

Write-Host ''
Write-Host ("Билд готов: {0}" -f $OutDir) -ForegroundColor Green
Write-Host ("  {0}, {1}, суммарно {2} МБ" -f $Runtime, $mode, $sizeMb)
Write-Host ("  запуск: {0}" -f $exe)
if ($zipPath) { Write-Host ("  архив:  {0}" -f $zipPath) }
