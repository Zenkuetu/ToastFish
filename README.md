# ToastFish v3.0

> 一款让你随时随地、被动地学习英语的 Windows 背单词工具。

本版本基于 [Uahh/ToastFish](https://github.com/Uahh/ToastFish)（MIT License）二次开发。

---

## 简介

ToastFish 是一款**被动式**英语 / 日语学习工具——它常驻系统托盘，主动弹出单词，你只需随手按下热键作答，无需刻意腾出整块时间，在刷手机、等编译、摸鱼的间隙即可无痛积累词汇量。基于 WPF（.NET Framework 4.7.2），使用 SM2+ 间隔重复算法，以 WinForms 通知弹窗展示单词及选项。

## 主要功能

- **通知弹窗背词**：WinForms 自绘弹窗（适配 Windows 11），支持英语 / 日语 / 五十音
- **SM2+ 间隔重复**：经调优的记忆算法，控制合意难度区间
- **AI 阅读模式**：学后 AI 短文阅读、15 选 10 完形填空（DeepSeek API）
- **惊喜复习 & 学后微阅读**：独立于主学习流程的巩固环节
- **学习记录仪表盘**：可视化学习数据（Vue 3 + Chart.js）
- **热键作答**：`ALT+Q` 开始学习，`ALT+1~4` 答题，`ALT+H` 显示/隐藏弹窗
- **开机自启动**、发音朗读、Excel 日志导入导出

## 目录结构

```
ToastFish/                        ← 运行目录（编译产物 + 运行资源）
Source/ToastFish-main/            ← C# 源码（.sln / .csproj / Model / View）
Release/                          ← 第三方 DLL 仓库（csproj 编译引用）
Tools/                            ← 数据工具与仪表盘脚本（Python）
Installer/                        ← Inno Setup 安装包构建脚本
```

## 编译

环境要求：VS Build Tools（含 .NET 桌面生成工具）、.NET Framework 4.7.2。

```bash
# 命令行编译（相对仓库根）
"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe" "Source\ToastFish-main\ToastFish.sln" -t:Build -p:Configuration=Release -p:Platform="Any CPU"
```

编译产物输出至 `Source/ToastFish-main/bin/Release/`。运行程序依赖 `Resources/inami.db`（纯净词库，已包含在本仓库）。

> 注意：源码 `ToastFish.csproj` 通过 `..\..\Release\` 相对路径引用第三方 DLL，请保持 `Release/` 目录与 `Source/` 的相对位置不变。

## 数据库说明

- 仓库内的 `Source/ToastFish-main/Resources/inami.db` 为**纯净词库**（无学习记录）。
- 学习数据（进度、SM2+ 参数）保存在本地运行目录的 `Resources/inami.db`，已通过 `.gitignore` 排除，不会上传。

## 许可证

本项目遵循原项目的 [MIT License](Source/ToastFish-main/LICENSE)，版权归原作者 [Uahh](https://github.com/Uahh/ToastFish) 所有。二次开发部分同样以 MIT 协议发布。

详细的使用说明、二次开发说明与第三方组件清单，见 [Source/ToastFish-main/README.md](Source/ToastFish-main/README.md)。
