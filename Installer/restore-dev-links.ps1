$desktop = [Environment]::GetFolderPath("Desktop")
$ws = New-Object -ComObject WScript.Shell

# Desktop
$sc = $ws.CreateShortcut("$desktop\ToastFish.lnk")
$sc.TargetPath = "E:\ToastFish.v3.0\ToastFish\ToastFish.exe"
$sc.WorkingDirectory = "E:\ToastFish.v3.0\ToastFish"
$sc.Save()
Write-Output "Desktop: $desktop\ToastFish.lnk"

# Start Menu
$programs = [Environment]::GetFolderPath("Programs")
$smDir = "$programs\ToastFish"
if (!(Test-Path $smDir)) { New-Item -ItemType Directory -Path $smDir | Out-Null }
$sc2 = $ws.CreateShortcut("$smDir\ToastFish.lnk")
$sc2.TargetPath = "E:\ToastFish.v3.0\ToastFish\ToastFish.exe"
$sc2.WorkingDirectory = "E:\ToastFish.v3.0\ToastFish"
$sc2.Save()
Write-Output "StartMenu: $smDir\ToastFish.lnk"
