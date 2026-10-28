# 精简便携发布目录（在 dotnet publish 之后运行）
# 用途：删除非中英语言、Node（CloudTV 登录制不需要）、WindowsAppSDK ML/AI、
#       WPF/WinForms 运行时。已用隐藏桌面启动 + 搜索 + 聚合详情验证。
# 用法：powershell -ExecutionPolicy Bypass -File scripts\trim-portable.ps1 -Path dist-lite

param(
    [string]$Path = "$PSScriptRoot\..\dist"
)

$ErrorActionPreference = "Stop"
$resolved = (Resolve-Path $Path).Path
$before = (Get-ChildItem -Path $resolved -Recurse -File | Measure-Object Length -Sum).Sum

# 保留：assets / ffmpeg / WinUI 资源 / 结构化发布的 libs、locales、runtime；其余非中英目录删除
Get-ChildItem -Path $resolved -Directory | Where-Object {
    $_.Name -notin @('assets', 'ffmpeg', 'Microsoft.UI.Xaml', 'libs', 'locales', 'runtime') -and
    $_.Name -notmatch '^(en|zh)'
} | Remove-Item -Recurse -Force

# WindowsAppSDK 未使用的 ML / AI / Widgets / NPU 检测
Get-ChildItem -Path $resolved -Directory -Filter 'NpuDetect' | Remove-Item -Recurse -Force
$patterns = @(
    'onnxruntime*.dll',
    'DirectML.dll',
    'Microsoft.ML.OnnxRuntime.dll',
    'Microsoft.Windows.AI.*',
    'Microsoft.Windows.Widgets.*'
)
foreach ($pattern in $patterns) {
    Get-ChildItem -Path $resolved -Filter $pattern -File | Remove-Item -Force
}

$after = (Get-ChildItem -Path $resolved -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Trimmed: {0:N1} MB -> {1:N1} MB (saved {2:N1} MB)" -f ($before / 1MB), ($after / 1MB), (($before - $after) / 1MB))
