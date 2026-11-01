param(
    [string]$Configuration = "Release",
    [switch]$StandaloneOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = $env:DOTNET_PATH
if (-not $dotnet) { $dotnet = "dotnet" }

function Build($label, $outDir, $extra) {
    Write-Host "=== $label ===" -ForegroundColor Cyan
    $args = @("publish", "$root\GeLiLauncher.csproj", "-c", $Configuration, "-r", "win-x64",
              "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
              "-p:DebugType=none", "-o", $outDir) + $extra
    & $dotnet @args | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$label 发布失败" }

    $exe = Join-Path $outDir "GeLiLauncher.exe"
    if (Test-Path $exe) {
        $target = Join-Path $outDir "GeLi Launcher.exe"
        Move-Item -Force $exe $target
        $size = [math]::Round((Get-Item $target).Length / 1KB, 0)
        Write-Host ("  -> {0}  {1} KB" -f $target, $size) -ForegroundColor Green
    }
}

# 1) 依赖系统 .NET 8 桌面运行时：exe 只有几百 KB
if (-not $StandaloneOnly) {
    Build "框架依赖（体积最小，需要 .NET 8 桌面运行时）" "$root\dist" @("-p:SelfContained=false")
}

# 2) 自包含单文件：无需安装运行时，体积大
Build "自包含单文件（免安装运行时）" "$root\dist\standalone" @("-p:SelfContained=true", "-p:EnableCompressionInSingleFile=true")

Write-Host "完成" -ForegroundColor Green
