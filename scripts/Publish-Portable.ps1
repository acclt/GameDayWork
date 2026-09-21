param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$packageName = "GameDayWork-v$Version-win-x64-portable"
$publishDirectory = Join-Path $artifactsRoot $packageName
$zipPath = Join-Path $artifactsRoot "$packageName.zip"
$servicePublishDirectory = Join-Path $artifactsRoot "$packageName-service"

$resolvedProjectRoot = [System.IO.Path]::GetFullPath($projectRoot)
$resolvedPublishDirectory = [System.IO.Path]::GetFullPath($publishDirectory)
if (-not $resolvedPublishDirectory.StartsWith($resolvedProjectRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录不在项目目录内：$resolvedPublishDirectory"
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path -LiteralPath $servicePublishDirectory) { Remove-Item -LiteralPath $servicePublishDirectory -Recurse -Force }

dotnet publish (Join-Path $projectRoot 'GameDayWork.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDirectory `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码：$LASTEXITCODE" }

dotnet publish (Join-Path $projectRoot 'GameDayWork.Service\GameDayWork.Service.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $servicePublishDirectory `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) { throw "服务程序 dotnet publish 失败，退出码：$LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $servicePublishDirectory 'GameDayWork.Service.exe') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\卸载系统服务.cmd') -Destination $publishDirectory
Remove-Item -LiteralPath $servicePublishDirectory -Recurse -Force

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output $zipPath
