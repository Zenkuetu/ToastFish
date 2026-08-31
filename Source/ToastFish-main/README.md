<p align="center">
  <img src="Resources/chika128.ico" width="128" height="128" alt="图标"/>
</p>

<div align="center">
  
# ToastFish

#### 这是一个利用Windows通知栏背单词的软件
#### 可以让你在上班、上课等恶劣环境下安全隐蔽地背单词

![License MIT](https://img.shields.io/badge/license-MIT-orange)
![GitHub release (latest by date)](https://img.shields.io/badge/release-v3.0-blue)
![GitHub issues](https://img.shields.io/github/issues/Uahh/ToastFish)
[![.NET Build & Test](https://github.com/Uahh/ToastFish/actions/workflows/dotnet-desktop.yml/badge.svg)](https://github.com/Uahh/ToastFish/actions/workflows/dotnet-desktop.yml)

</div>

## 使用方法
### 基本流程
1. 选择词库：  
  
![选择词库](https://github.com/Uahh/ToastFish/blob/main/Resources/Gif/选择词库.gif)  

2. 设置背诵单词数量：  
  
![设置词数](https://github.com/Uahh/ToastFish/blob/main/Resources/Gif/选择数量.gif)  

3. 点击开始：  
  
![设置词数](https://github.com/Uahh/ToastFish/blob/main/Resources/Gif/开始.gif)  

4. 背完之后会有测试：  
  
![设置词数](https://github.com/Uahh/ToastFish/blob/main/Resources/Gif/测试.gif)  

### 背诵记录
每一次点击开始都会有记录，文件格式为xlsx。位于安装目录的Log文件夹下。  

### 导入单词
可以将背诵记录导入，重新背诵。  
  
![设置词数](https://github.com/Uahh/ToastFish/blob/main/Resources/Gif/导入单词.gif)  

### 自定义内容
可以通过自定义Excel内容来让ToastFish推送所需要的内容。  
自定义Excel模板位于安装目录/Resources/自定义模板.xslx  
  
![设置词数](https://github.com/Uahh/ToastFish/blob/main/Resources/Gif/导入自定义单词.gif)  

### 操作系统要求
Windows 10 及以上
### Q&A
Q: 每次通知停留时间太短了，如何设置停留时间？  
A: 可以在系统设置 -> 轻松使用 -> 显示 -> 通知显示的时间 里设置停留时间  
  
Q: 使用英语发音功能时会闪退？  
A: 请在系统设置里下载英语语音包，重启软件即可解决。  
  
Q: 有没有Win7或是Mac版本的开发计划？  
A: 这个真没有，Win7和Mac没有Windows10的通知栏。  
  
Q: 没有我想要背的单词怎么办？  
A: 可以使用自定义功能自己构建单词列表，如果单词数量很多，可以联系作者帮忙添加。  
  
Q: 遇到了其他困难或是想给软件提建议？  
A: 可以提Issue，将问题或建议提供给我。  
  
Q: 软件收费吗？  
A: 软件完全开源且免费。  

## 下载与安装
1. 可以去网盘下载，下载双击安装ToastFishSetup.exe即可。  
```bash
链接：https://pan.baidu.com/s/1VlnJSSbEgcNErV-gy3um6w
提取码：2173 
```
2. 也可以去项目Tag处下载Release版本，解压即可免安装运行。

## 编译源码
请在cmd中运行
```bash
git clone https://github.com/Uahh/ToastFish
```
项目使用VS2019, .net环境为4.7.2.

## 感谢

感谢@itorr为本软件提供的支持、建议和测试！

---

## 二次开发说明

本版本 **ToastFish v3.0** 基于 [Uahh/ToastFish](https://github.com/Uahh/ToastFish) 二次开发，遵循原项目 [MIT License](LICENSE)。

主要改动与新增：

- **通知系统**：弃用 UWP Toast，自研 WinForms 弹窗（适配 Windows 11）
- **AI 功能**：学后 AI 短文阅读、15 选 10 完形填空（DeepSeek API）
- **学习增强**：惊喜复习、学后微阅读、SM2+ 间隔重复算法调优
- **可视化**：学习记录仪表盘（Vue 3 + Chart.js）
- **其它**：开机自启动、五十音发音、安装包构建脚本

## 第三方组件

本项目引用了以下第三方库（存放在 `Release/` 目录，经 `ToastFish.csproj` 相对路径引用）。各库遵循其自身开源许可证，完整条款请参见各库官网：

| 组件 | 用途 | 许可证 |
|------|------|--------|
| NPOI | Excel 日志读写 | Apache-2.0 |
| System.Data.SQLite | SQLite 数据库访问 | Public Domain |
| EntityFramework 6 | 数据访问 | Apache-2.0 |
| Dapper | 轻量 ORM | Apache-2.0 / MIT |
| MVVM Light (GalaSoft) | MVVM 框架 | MIT |
| Prism | 组合式 UI 框架 | MIT |
| Reactive Extensions (Rx) | 响应式编程 | MIT |
| BouncyCastle.Crypto | 加密 | MIT |
| SharpZipLib | 压缩 | MIT |
| MP3Sharp | MP3 解码 | MIT |
| Microsoft.Toolkit.Uwp.Notifications | 通知（已废弃保留） | MIT |
| CommonServiceLocator | 服务定位 | MS-PL |

## 许可证

- 本项目整体遵循 [MIT License](LICENSE)，版权归原作者 [Uahh](https://github.com/Uahh/ToastFish)。
- 二次开发部分同样以 MIT 协议发布，使用时请保留上述版权与许可声明。
