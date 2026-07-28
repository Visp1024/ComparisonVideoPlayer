<#
    Собирает инсталлятор CVP: сначала публикационный билд
    (tools/publish.ps1), затем Inno Setup поверх него.

    Запуск (Windows PowerShell 5.1 или pwsh):
        powershell -ExecutionPolicy Bypass -File tools/make-installer.ps1

    Полезные ключи:
        -SkipPublish        не пересобирать билд, взять готовый из publish/<runtime>
        -SourceDir <путь>   явный каталог с собранным приложением
        -Iscc <путь>        явный путь к ISCC.exe, если он не в PATH и не в Program Files
        -FFmpegDir <путь>   каталог FFmpeg для билда (передаётся в publish.ps1)

    Inno Setup 6 нужен отдельно: https://jrsoftware.org/isdl.php
    (или winget install JRSoftware.InnoSetup)
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [string] $SourceDir     = '',
    [string] $OutDir        = '',
    [string] $FFmpegDir     = '',
    [string] $Iscc          = '',
    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tools = Split-Path -Parent $PSCommandPath
$root  = Resolve-Path -LiteralPath (Join-Path $tools '..')
$iss   = Join-Path $tools 'installer.iss'
if (-not (Test-Path -LiteralPath $iss)) { throw "не найден скрипт инсталлятора: $iss" }

if (-not $SourceDir) { $SourceDir = Join-Path $root "publish\$Runtime" }
if (-not $OutDir)    { $OutDir    = Join-Path $root 'publish\installer' }

# 1. Публикационный билд.
if ($SkipPublish) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir 'ComparisonPlayer.exe'))) {
        throw "-SkipPublish задан, но готового билда нет: $SourceDir"
    }
    Write-Host "билд не пересобираю: $SourceDir" -ForegroundColor DarkGray
}
else {
    $publishArgs = @{ Configuration = $Configuration; Runtime = $Runtime; OutDir = $SourceDir }
    if ($FFmpegDir) { $publishArgs['FFmpegDir'] = $FFmpegDir }
    & (Join-Path $tools 'publish.ps1') @publishArgs
}

$exe = Join-Path $SourceDir 'ComparisonPlayer.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "в билде нет exe: $exe" }

$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if (-not $version) { $version = '1.0.0' }
$version = ($version -split '\+')[0]

# 2. Компилятор Inno Setup: ключ -> PATH -> обычные места установки.
if (-not $Iscc) {
    # @(...) обязательно: единственный найденный путь иначе станет строкой,
    # и [0] вернёт первый символ вместо файла.
    $candidates = @(@(
        (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($candidates.Count -gt 0) { $Iscc = $candidates[0] }
}

if (-not $Iscc) {
    throw @'
не найден ISCC.exe (компилятор Inno Setup 6).

Установите Inno Setup — winget install JRSoftware.InnoSetup — либо укажите путь ключом:
    tools/make-installer.ps1 -Iscc "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

Готовый билд при этом уже собран и работоспособен как переносимая поставка:
запустите tools/publish.ps1 -Zip, чтобы получить архив без инсталлятора.
'@
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$isccArgs = @(
    "/DAppVersion=$version",
    "/DSourceDir=$SourceDir",
    "/DOutputDir=$OutDir",
    $iss
)

Write-Host "$Iscc $($isccArgs -join ' ')" -ForegroundColor DarkGray
& $Iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }

$setup = Join-Path $OutDir "CVP-$version-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC отработал, но инсталлятора нет: $setup" }

$sizeMb = [math]::Round(((Get-Item -LiteralPath $setup).Length / 1MB), 1)

Write-Host ''
Write-Host ("Инсталлятор готов: {0}" -f $setup) -ForegroundColor Green
Write-Host ("  версия {0}, {1} МБ" -f $version, $sizeMb)
