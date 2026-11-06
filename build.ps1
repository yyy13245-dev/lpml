# LPML 构建脚本：常规多文件发布（不做任何目录整理，.NET 默认布局）
$ErrorActionPreference = "Stop"

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    # 目标机没装 .NET 10 运行时时改为 $true
    [bool]$SelfContained = $false
)

$project = "src\LPML.App\LPML.App.csproj"
$output  = "publish\$($Configuration.ToLower())"

Write-Host "清理旧产物…" -ForegroundColor Cyan
if (Test-Path $output) { Remove-Item $output -Recurse -Force }

Write-Host "发布 LPML（$Configuration / win-x64 / 多文件）…" -ForegroundColor Cyan
dotnet publish $project `
  -c $Configuration `
  -r win-x64 `
  --self-contained $SelfContained `
  -o $output

if ($LASTEXITCODE -ne 0) { throw "发布失败" }

Write-Host ""
Write-Host "完成：$output\LPML.exe" -ForegroundColor Green
Write-Host "首次运行后会在根目录生成：.minecraft\（游戏数据）与 LPML\（配置 / 缓存 / 日志 / 实例）。" -ForegroundColor Green
