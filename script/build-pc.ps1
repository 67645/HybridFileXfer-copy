# 构建 PC 客户端与 GUI 引擎 jar
# 用法：powershell -ExecutionPolicy Bypass -File script/build-pc.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'HybridFileXfer-PC/src'
$build = Join-Path $root 'HybridFileXfer-PC/build'
$classes = Join-Path $build 'classes'
$jarPath = Join-Path $build 'HybridFileXfer.jar'

if (-not (Get-Command javac -ErrorAction SilentlyContinue)) {
    throw '未找到 javac，请安装 JDK 17+ 并加入 PATH'
}
if (-not (Get-Command jar -ErrorAction SilentlyContinue)) {
    throw '未找到 jar，请安装 JDK 17+ 并加入 PATH'
}

Remove-Item -Recurse -Force $classes -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $classes | Out-Null

$files = Get-ChildItem -Recurse -File $src -Filter *.java | ForEach-Object { $_.FullName }
if ($files.Count -eq 0) {
    throw "未找到 Java 源文件：$src"
}

Write-Host "编译 $($files.Count) 个 Java 文件..."
& javac -encoding UTF-8 --release 8 -Xlint:-options -implicit:none -d $classes $files
if ($LASTEXITCODE -ne 0) {
    throw "javac 失败，退出码 $LASTEXITCODE"
}

Copy-Item -Path (Join-Path $src 'messages_*.properties') -Destination $classes -Force

New-Item -ItemType Directory -Force $build | Out-Null
Remove-Item -Force $jarPath -ErrorAction SilentlyContinue
Write-Host "打包 $jarPath ..."
& jar cfe $jarPath top.weixiansen574.hybridfilexfer.Main -C $classes .
if ($LASTEXITCODE -ne 0) {
    throw "jar 失败，退出码 $LASTEXITCODE"
}

$size = [math]::Round((Get-Item $jarPath).Length / 1MB, 2)
Write-Host "完成：$jarPath ($size MB)"
