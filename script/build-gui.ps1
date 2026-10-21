# 构建 WinUI 3 GUI 发布包（不内置 JRE，运行时需要系统 Java 17+）
# 用法：powershell -ExecutionPolicy Bypass -File script/build-gui.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'HybridFileXfer-Windows/HybridFileXfer.Gui/HybridFileXfer.Gui.csproj'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'gui'
$zip = Join-Path $dist 'HybridFileXfer-Gui-win-x64.zip'

if (-not (Get-Command javac -ErrorAction SilentlyContinue)) {
    throw '未找到 javac，请安装 JDK 17+ 并加入 PATH'
}

$dotnetPath = $null
$localDotnet = Join-Path $env:LOCALAPPDATA 'dotnet-sdk8\dotnet.exe'
if ($env:DOTNET_ROOT) {
    $rootDotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    if (Test-Path $rootDotnet) {
        $dotnetPath = $rootDotnet
    }
}
if (-not $dotnetPath -and (Test-Path $localDotnet)) {
    $dotnetPath = $localDotnet
}
if (-not $dotnetPath) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) {
        $dotnetPath = $dotnetCommand.Source
    }
}
if (-not $dotnetPath) {
    throw '未找到 dotnet，请安装 .NET 8 SDK 并加入 PATH'
}
$sdks = & $dotnetPath --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^8\.' })) {
    throw "当前 dotnet 没有 .NET 8 SDK：$dotnetPath"
}

Write-Host '1/3 构建 Java 引擎...'
& (Join-Path $PSScriptRoot 'build-pc.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "Java 引擎构建失败，退出码 $LASTEXITCODE"
}

Write-Host '2/3 发布 WinUI 3 GUI...'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null
& $dotnetPath publish $project `
    -c Release `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -o $out
if ($LASTEXITCODE -ne 0) {
    throw "GUI 发布失败，退出码 $LASTEXITCODE"
}

$required = @('HybridFileXfer.Gui.exe', 'HybridFileXfer.jar', 'adb.exe')
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $out $name))) {
        throw "发布目录缺少 $name"
    }
}

Write-Host '3/3 打包 zip...'
Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "完成：$zip ($size MB)"
Write-Host '注意：发布包不包含 JRE，运行前请确保系统 java 17+ 可用。'
