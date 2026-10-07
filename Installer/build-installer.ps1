<#
.SYNOPSIS
    ToastFish 安装包一键构建脚本
.DESCRIPTION
    自动完成：编译 C# 源码 → 准备打包文件 → 编译 Inno Setup 安装包
    使用方式: powershell -NoProfile -File build-installer.ps1
.NOTES
    文件名: build-installer.ps1
#>

$ErrorActionPreference = "Stop"

# ============ 路径配置 ============
$SourceDir   = "E:\ToastFish.v3.0\Source\ToastFish-main"
$RunDir      = "E:\ToastFish.v3.0\ToastFish"
$StagingDir  = "E:\ToastFish.v3.0\Installer\staging"
$InstallerDir = "E:\ToastFish.v3.0\Installer"
$IssFile     = "$InstallerDir\toastfish.iss"
$MSBuild     = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
$ISCC        = "C:\Users\Cyansu\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
$SlnFile     = "$SourceDir\ToastFish.sln"

# ============ 版本号：从 toastfish.iss 读取（唯一来源），推导安装包文件名 ============
$IssVersionLine = Select-String -Path $IssFile -Pattern '^#define MyAppVersion "([^"]+)"' -Encoding UTF8 | Select-Object -First 1
if (-not $IssVersionLine) {
    Write-Host "❌ 无法从 toastfish.iss 解析 MyAppVersion" -ForegroundColor Red
    exit 1
}
$AppVersion = $IssVersionLine.Matches[0].Groups[1].Value
$SetupName  = "ToastFish-v$AppVersion-Setup.exe"
Write-Host "  版本: v$AppVersion    安装包: $SetupName" -ForegroundColor Cyan

# ============ 步骤 1: 编译 C# 项目 ============
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  步骤 1/5: 编译 C# 项目" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$buildResult = & $MSBuild $SlnFile -t:Build -p:Configuration=Release -p:Platform="Any CPU" -v:minimal 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 编译失败！" -ForegroundColor Red
    Write-Host $buildResult
    exit 1
}
Write-Host "✅ 编译成功" -ForegroundColor Green

# ============ 步骤 2: 准备打包文件 ============
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  步骤 2/5: 准备打包文件" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 2a. 清空 staging
Write-Host "  → 清理 staging 目录..."
if (Test-Path $StagingDir) {
    Remove-Item -Recurse -Force "$StagingDir\*"
} else {
    New-Item -ItemType Directory -Path $StagingDir | Out-Null
}

# 2b. 从编译输出复制主程序
$BuildOutput = "$SourceDir\bin\Release"
Write-Host "  → 从编译输出复制主程序..."
Copy-Item "$BuildOutput\ToastFish.exe"        $StagingDir
Copy-Item "$BuildOutput\ToastFish.exe.config" $StagingDir
Copy-Item "$BuildOutput\ToastFish.exe.manifest" $StagingDir
# .pdb 可选（调试用，不打入安装包可省略）
if (Test-Path "$BuildOutput\ToastFish.pdb") {
    Copy-Item "$BuildOutput\ToastFish.pdb" $StagingDir
}

# 2c. 从运行目录复制 DLL 依赖（第三方库不变，直接用运行目录的快照）
Write-Host "  → 复制 DLL 依赖..."
Copy-Item "$RunDir\*.dll" $StagingDir

# 2d. 复制 SQLite 原生互操作
Write-Host "  → 复制 SQLite 互操作 DLL..."
Copy-Item -Recurse "$RunDir\x64" $StagingDir
Copy-Item -Recurse "$RunDir\x86" $StagingDir

# 2e. 复制 Resources（排除运行时垃圾）
Write-Host "  → 复制 Resources（清理运行时垃圾）..."
Copy-Item -Recurse "$RunDir\Resources" $StagingDir

# 复制仪表盘生成器到 Resources（2026-07-22）
Write-Host "  → 复制仪表盘生成器..."
Copy-Item "E:\ToastFish.v3.0\Tools\generate_dashboard.py" "$StagingDir\Resources\"
Copy-Item "E:\ToastFish.v3.0\Tools\dashboard.template.html" "$StagingDir\Resources\"
Copy-Item "E:\ToastFish.v3.0\Tools\essay_api.py" "$StagingDir\Resources\"

# 2e-2. 内置 Python 运行时（issue #2：全新电脑没有 Python，仪表盘生成脚本无法执行）
Write-Host "  → 准备内置 Python 运行时..."
$PyCache = "$InstallerDir\cache\python"
$PyZip   = "$InstallerDir\cache\python-3.11.9-embed-amd64.zip"
$PyUrl   = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip"
if (-not (Test-Path "$PyCache\python.exe")) {
    if (-not (Test-Path $PyZip)) {
        Write-Host "    → 首次构建：下载 $PyUrl"
        New-Item -ItemType Directory -Path "$InstallerDir\cache" -Force | Out-Null
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try {
            Invoke-WebRequest -Uri $PyUrl -OutFile $PyZip -UseBasicParsing
        } catch {
            Write-Host "❌ 下载 Python 运行时失败：$($_.Exception.Message)" -ForegroundColor Red
            Write-Host "  请手动下载 $PyUrl 并保存为：$PyZip" -ForegroundColor Yellow
            exit 1
        }
    }
    Expand-Archive -Path $PyZip -DestinationPath $PyCache -Force
}
# 精简：仪表盘只需本地 SQLite + HTTP，去掉 TLS 相关组件（约 -6MB）
@("libcrypto-3.dll", "libssl-3.dll", "_ssl.pyd", "_hashlib.pyd") | ForEach-Object {
    Remove-Item "$PyCache\$_" -Force -ErrorAction SilentlyContinue
}
# 覆盖可能已随 Resources 复制进来的旧副本，保证 staging 用缓存版本
Remove-Item "$StagingDir\Resources\python" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Recurse $PyCache "$StagingDir\Resources\python"
Write-Host "    内置运行时已就绪（$([math]::Round((Get-ChildItem "$StagingDir\Resources\python" -Recurse -File | Measure-Object Length -Sum).Sum/1MB,1)) MB）" -ForegroundColor White

# 清理数据库运行时残留 + 用户隐私文件（ai_config.txt 含 API Key，绝不能进安装包）
$resDir = "$StagingDir\Resources"
@("*.db-shm", "*.db-wal", "*.db.bak", "*.db.*_fix_bak", "*.db.sentence_fix_bak", "*.db.phonetic_bak*", ".sm2_fixed", "ai_config.txt", "ai_diag.log", "dashboard.html") | ForEach-Object {
    Remove-Item "$resDir\$_" -Force -ErrorAction SilentlyContinue
}

# 清理不必要的文件（减小安装包体积）
Remove-Item "$StagingDir\Microsoft.Toolkit.Uwp.Notifications.dll" -Force -ErrorAction SilentlyContinue

# 2f. 清洗数据库：重置所有学习数据，保留词汇内容
Write-Host "  → 清洗数据库（重置学习数据，保留词汇）..."
$ResetScript = "E:\ToastFish.v3.0\Tools\reset_database_for_distribution.py"
$StagingDb = "$StagingDir\Resources\inami.db"
$null = & python $ResetScript $StagingDb 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 数据库清洗失败！" -ForegroundColor Red
    exit 1
}
Write-Host "    数据库已清洗: 全部学习记录已重置" -ForegroundColor White

Write-Host "✅ 打包文件准备完成" -ForegroundColor Green

# ============ 步骤 3: 编译安装包 ============
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  步骤 3/5: 编译 Inno Setup 安装包" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

Write-Host "  → 运行 ISCC..."
$issResult = & $ISCC $IssFile 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 安装包编译失败！" -ForegroundColor Red
    Write-Host $issResult
    exit 1
}

# 过滤掉压缩进度行，只显示摘要
$issResult -split "`n" | ForEach-Object {
    $line = $_.Trim()
    if ($line -match "Successful compile|Warning:|Error") {
        Write-Host "  $line"
    }
}

# ============ 步骤 4: 验证产物 ============
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  步骤 4/5: 验证产物" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$setupExe = "$InstallerDir\$SetupName"
if (Test-Path $setupExe) {
    $size = (Get-Item $setupExe).Length
    $sizeMB = [math]::Round($size / 1MB, 1)
    Write-Host "✅ 安装包生成成功！" -ForegroundColor Green
    Write-Host "   文件: $setupExe" -ForegroundColor White
    Write-Host "   大小: $sizeMB MB" -ForegroundColor White
}

# ============ 完成 ============
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  构建完成！安装包位于:" -ForegroundColor Green
Write-Host "  $setupExe" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Green

# 可选：询问是否立即测试安装
Write-Host ""
Write-Host "提示：运行 '$setupExe' 即可测试安装" -ForegroundColor Gray
Write-Host "静默安装: '$setupExe /VERYSILENT /SUPPRESSMSGBOXES'" -ForegroundColor Gray
