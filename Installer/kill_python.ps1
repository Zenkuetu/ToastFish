# 结束 ToastFish 自带 Python 运行时启动的进程（按可执行文件路径 / 命令行匹配）。
# 只用路径判断，绝不按进程名杀 —— 用户自己的 Python 在别的目录，不会被动到。
param([string]$AppDir)
$ErrorActionPreference = 'SilentlyContinue'
if ([string]::IsNullOrWhiteSpace($AppDir)) { exit 0 }
$prefix = ($AppDir.TrimEnd('\') + '\Resources\python\').ToLower()
$hit = 0
Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'python.exe' -or $_.Name -eq 'pythonw.exe' } | ForEach-Object {
  $ep = ([string]$_.ExecutablePath).ToLower()
  $cl = ([string]$_.CommandLine).ToLower()
  $m = $false
  if ($ep -ne '' -and $ep.StartsWith($prefix)) { $m = $true }
  if (-not $m -and $cl -ne '' -and $cl.Contains($prefix)) { $m = $true }
  if ($m) { $hit++; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
if ($env:TOASTFISH_KILLDBG) { Add-Content -Path $env:TOASTFISH_KILLDBG -Value ("prefix=" + $prefix + " killed=" + $hit) }
exit 0