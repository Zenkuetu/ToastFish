; ToastFish v3.0 Inno Setup 安装脚本
; 编译器: ISCC.exe (Inno Setup 6.7+)
; 运行: "C:\Users\Cyansu\AppData\Local\Programs\Inno Setup 6\ISCC.exe" toastfish.iss

#define MyAppName "ToastFish"
#define MyAppVersion "3.0"
#define MyAppPublisher "ToastFish"
#define MyAppExeName "ToastFish.exe"
#define SourceRoot "E:\ToastFish.v3.0\Installer\staging"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) 2026 ToastFish
VersionInfoVersion=3.0.0.0
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=E:\ToastFish.v3.0\Installer
OutputBaseFilename=ToastFish-v3.0-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
DisableWelcomePage=no
DisableProgramGroupPage=yes
; 相对路径即可——ISCC 基于 .iss 所在目录解析
InfoBeforeFile=安装说明.txt
UninstallDisplayName=ToastFish v3.0
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=E:\ToastFish.v3.0\Source\ToastFish-main\chika64.ico

[Messages]
; ============ 窗口标题 ============
SetupWindowTitle=安装 - [name]
UninstallAppTitle=卸载
UninstallAppFullTitle=[name] 卸载

; ============ 对话框 ============
InformationTitle=信息
ConfirmTitle=确认
ErrorTitle=错误
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。如果现在退出，程序将不会被安装。%n%n你可以稍后再运行安装程序完成安装。%n%n确定要退出吗？
AboutSetupMenuItem=关于安装程序(&A)...
AboutSetupTitle=关于安装程序
AboutSetupMessage=%1 版本 %2%n%3%n%n%1 主页:%n%4
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)
ClickNext=点击「下一步」继续，或点击「取消」退出安装。
BeveledLabel=

; ============ 1) 欢迎页 ============
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=即将安装 [name/ver] 到你的计算机。%n%n建议在继续前关闭其他应用程序。

; ============ 2) 信息页（安装手册） ============
WizardInfoBefore=安装说明
InfoBeforeLabel=请在继续前仔细阅读以下安装说明：
InfoBeforeClickLabel=我已阅读并理解上述安装说明(&R)

; ============ 3) 选择安装目录页 ============
WizardSelectDir=选择安装位置
SelectDirDesc=[name] 应该安装到哪里？
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。
SelectDirBrowseLabel=点击「下一步」继续。如需选择其他文件夹，点击「浏览」。
BrowseDialogTitle=浏览文件夹
BrowseDialogLabel=从下面的列表中选择一个文件夹，然后点击「确定」。
NewFolderName=新建文件夹
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
CannotInstallToNetworkDrive=无法安装到网络驱动器。
CannotInstallToUNCPath=无法安装到 UNC 路径。
InvalidPath=请输入完整路径（含盘符），例如：%n%nC:\APP%n%n或 UNC 路径：%n%n\\server\share
InvalidDrive=所选驱动器或 UNC 共享不存在或无法访问，请重新选择。
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=安装至少需要 %1 KB 可用空间，但所选驱动器仅剩 %2 KB。%n%n仍要继续吗？
DirNameTooLong=文件夹名称或路径过长。
InvalidDirName=文件夹名称无效。
DirExistsTitle=文件夹已存在
DirExists=文件夹%n%n%1%n%n已存在。是否仍安装到该文件夹？
DirDoesntExistTitle=文件夹不存在
DirDoesntExist=文件夹%n%n%1%n%n不存在。是否创建该文件夹？

; ============ 4) 选择组件页 ============
WizardSelectComponents=选择组件
SelectComponentsDesc=应安装哪些组件？
SelectComponentsLabel2=勾选要安装的组件，取消勾选不需要的组件。完成后点击「下一步」。
FullInstallation=标准安装
CompactInstallation=最小安装
CustomInstallation=自定义安装
ComponentsDiskSpaceMBLabel=当前选择至少需要 [mb] MB 磁盘空间。
ComponentSize1=%1 KB
ComponentSize2=%1 MB
NoUninstallWarningTitle=组件已存在
NoUninstallWarning=安装程序检测到以下组件已安装：%n%n%1%n%n取消勾选不会卸载它们。%n%n仍要继续吗？

; ============ 5) 选择任务页 ============
WizardSelectTasks=选择附加任务
SelectTasksDesc=应执行哪些附加任务？
SelectTasksLabel2=选择安装 [name] 时要执行的附加任务，然后点击「下一步」。

; ============ 6) 准备安装页 ============
WizardReady=准备安装
ReadyLabel1=安装程序已就绪，即将安装 [name] 到你的计算机。
ReadyLabel2a=点击「安装」开始安装，或点击「上一步」检查或修改设置。
ReadyLabel2b=点击「安装」开始安装。
ReadyMemoUserInfo=用户信息:
ReadyMemoDir=安装目录:
ReadyMemoType=安装类型:
ReadyMemoComponents=选定组件:
ReadyMemoGroup=开始菜单文件夹:
ReadyMemoTasks=附加任务:

; ============ 7) 正在安装页 ============
WizardInstalling=正在安装
InstallingLabel=请稍候，安装程序正在安装 [name] 到你的计算机。

; ============ 8) 安装完成页 ============
FinishedHeadingLabel=[name] 安装完成
FinishedLabel=安装程序已在你的计算机上完成 [name] 的安装。可以通过开始菜单或桌面快捷方式启动。
FinishedLabelNoIcons=安装程序已在你的计算机上完成 [name] 的安装。
ClickFinish=点击「完成」退出安装向导。
RunEntryExec=运行 %1
RunEntryShellExec=查看 %1
ShowReadmeCheck=是，我想查看自述文件
FinishedRestartLabel=为完成 [name] 的安装，需要重新启动计算机。是否立即重启？
FinishedRestartMessage=为完成 [name] 的安装，需要重新启动计算机。%n%n是否立即重启？
YesRadio=立即重启(&Y)
NoRadio=稍后手动重启(&N)

; ============ 安装进度状态 ============
StatusClosingApplications=正在关闭应用程序...
StatusCreateDirs=正在创建目录...
StatusExtractFiles=正在解压文件...
StatusCreateIcons=正在创建快捷方式...
StatusCreateIniEntries=正在创建配置...
StatusCreateRegistryEntries=正在写入注册表...
StatusRegisterFiles=正在注册文件...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成安装...
StatusRestartingApplications=正在重启应用程序...
StatusRollback=正在回滚更改...

; ============ 错误/提示 ============
SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请关闭所有实例后点击「确定」继续，或点击「取消」退出。
ErrorCreatingDir=安装程序无法创建目录 "%1"
ErrorInternal2=内部错误: %1
SetupAborted=安装未完成。%n%n请修复问题后重新运行安装程序。

; ============ 卸载界面 ============
ConfirmUninstall=确定要完全卸载 [name] 及其所有组件吗？
UninstallStatusLabel=请稍候，正在卸载 [name] ...
UninstalledAll=[name] 已成功从你的计算机中移除。
UninstalledMost=[name] 卸载完成。%n%n部分文件无法自动移除，可手动删除。
UninstalledAndNeedsRestart=为完成 [name] 的卸载，需要重新启动计算机。%n%n是否立即重启？
WizardUninstalling=卸载状态
StatusUninstalling=正在卸载 %1 ...

; ============ 卸载器错误 ============
UninstallNotFound=文件 "%1" 不存在，无法卸载。
UninstallOpenError=文件 "%1" 无法打开，无法卸载。
UninstallUnsupportedVer=卸载日志 "%1" 格式不被此版本卸载器支持。无法卸载。
UninstallDataCorrupted="%1" 文件已损坏，无法卸载。

[CustomMessages]
; 仅为 Inno Setup 不内置的消息键使用 CustomMessages

[Types]
Name: "full"; Description: "标准安装（推荐）"
Name: "compact"; Description: "最小安装（仅主程序）"
Name: "custom"; Description: "自定义安装"; Flags: iscustom

[Components]
Name: "main"; Description: "主程序（必需）"; Types: full compact custom; Flags: fixed
Name: "db"; Description: "单词数据库 (inami.db, ~22 MB)"; Types: full custom; ExtraDiskSpaceRequired: 23592960
Name: "audio"; Description: "日语五十音发音文件 (104 个 MP3)"; Types: full; ExtraDiskSpaceRequired: 5406720

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "额外快捷方式:"; Flags: checkedonce
Name: "launchafter"; Description: "安装完成后启动 ToastFish"; GroupDescription: "其他:"
Name: "migratedata"; Description: "导入旧版 ToastFish 学习数据（保留学习进度和记录）"; GroupDescription: "数据迁移:"; Flags: unchecked

[Files]
; 主程序
Source: "{#SourceRoot}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\*.config"; DestDir: "{app}"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\*.manifest"; DestDir: "{app}"; Flags: ignoreversion; Components: main

; DLL 依赖
Source: "{#SourceRoot}\*.dll"; DestDir: "{app}"; Flags: ignoreversion; Components: main

; SQLite 原生互操作
Source: "{#SourceRoot}\x64\*"; DestDir: "{app}\x64"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\x86\*"; DestDir: "{app}\x86"; Flags: ignoreversion; Components: main

; Resources - 数据库（重要：仅首次安装写入，永不卸载删除，保护用户学习数据）
Source: "{#SourceRoot}\Resources\inami.db"; DestDir: "{app}\Resources"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall; Components: db
Source: "{#SourceRoot}\Resources\mute.mp3"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\Star.pdf"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\使用说明.html"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\自定义模板.xlsx"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\Gif\*"; DestDir: "{app}\Resources\Gif"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\Goin\*"; DestDir: "{app}\Resources\Goin"; Flags: ignoreversion; Components: audio
; 仪表盘（学习报告）依赖
Source: "{#SourceRoot}\Resources\vue.min.js"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\chart.min.js"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\dashboard.template.html"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\generate_dashboard.py"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
Source: "{#SourceRoot}\Resources\essay_api.py"; DestDir: "{app}\Resources"; Flags: ignoreversion; Components: main
; 内置 Python 运行时（issue #2：全新电脑没有 Python，仪表盘生成脚本无法执行）
Source: "{#SourceRoot}\Resources\python\*"; DestDir: "{app}\Resources\python"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: main

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 ToastFish"; Flags: nowait postinstall skipifsilent; Tasks: launchafter

[Code]
var
  IsFirstDbInstall: Boolean;
  MigrationPage: TInputFileWizardPage;

// 强制关闭运行中的 ToastFish（失败不中止）
procedure KillToastFish;
var
  ResultCode: Integer;
begin
  Exec('taskkill', '/F /IM ToastFish.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  KillToastFish;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  KillToastFish;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillToastFish;
  Result := '';
end;

// 自动搜索可能的旧数据库位置
function AutoDetectOldDb: String;
var
  DevPath, InstalledPath: String;
begin
  Result := '';
  DevPath := 'E:\ToastFish.v3.0\ToastFish\Resources\inami.db';
  InstalledPath := ExpandConstant('{localappdata}\ToastFish\Resources\inami.db');

  if FileExists(DevPath) then
    Result := DevPath
  else if FileExists(InstalledPath) then
    Result := InstalledPath;
end;

// 在"选择附加任务"页之后创建数据迁移文件选择页
procedure InitializeWizard;
var
  DetectedPath: String;
begin
  DetectedPath := AutoDetectOldDb;

  MigrationPage := CreateInputFilePage(
    wpSelectTasks,
    '导入旧学习数据',
    '找到旧版 ToastFish 的数据库文件，保留你的学习进度和记录。',
    '如果不导入旧数据，请留空并点击"下一步"。' + #13#10#13#10 +
    '提示：数据库文件名为 inami.db，位于旧版 ToastFish 的 Resources 文件夹中。');

  MigrationPage.Add('数据库文件:', 'SQLite 数据库 (*.db)|*.db|所有文件|*.*', '.db');

  if DetectedPath <> '' then
    MigrationPage.Values[0] := DetectedPath;
end;

// 仅勾选了"导入旧数据"时才显示迁移页面
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  if PageID = MigrationPage.ID then
    Result := not WizardIsTaskSelected('migratedata')
  else
    Result := False;
end;

// ssInstall 阶段 {app} 已可用，在此记录是否有旧安装
procedure CurStepChanged(CurStep: TSetupStep);
var
  OldDbPath, NewDbPath: String;
  Migrated: Boolean;
begin
  if CurStep = ssInstall then
  begin
    IsFirstDbInstall := not FileExists(ExpandConstant('{app}\Resources\inami.db'));
  end;

  if CurStep = ssPostInstall then
  begin
    // 覆盖安装不迁移（数据库已有数据）
    if not IsFirstDbInstall then
      Exit;

    // 用户未勾选迁移任务 → 使用空白数据库
    if not WizardIsTaskSelected('migratedata') then
      Exit;

    OldDbPath := MigrationPage.Values[0];
    if OldDbPath = '' then
      Exit;

    NewDbPath := ExpandConstant('{app}\Resources\inami.db');

    // 验证用户选择的文件
    if not FileExists(OldDbPath) then
    begin
      MsgBox('未找到指定的数据库文件：' + #13#10 + OldDbPath + #13#10#13#10 +
             '将使用空白数据库开始。', mbInformation, MB_OK);
      Exit;
    end;

    // 执行迁移
    Migrated := CopyFile(OldDbPath, NewDbPath, False);
    if Migrated then
    begin
      MsgBox('学习数据导入成功！' + #13#10#13#10 +
             '位置：' + NewDbPath + #13#10 +
             '学习进度、SM2+ 参数和统计记录已全部保留。',
             mbInformation, MB_OK);
    end
    else
    begin
      MsgBox('导入失败：无法复制数据库文件。' + #13#10#13#10 +
             '可能原因：磁盘空间不足或权限不够。' + #13#10 +
             '将使用空白数据库开始。',
             mbError, MB_OK);
    end;
  end;
end;
