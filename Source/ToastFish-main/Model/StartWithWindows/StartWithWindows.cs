using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;

namespace ToastFish.Model.StartWithWindows
{
    /// <summary>
    /// 开机自启动管理 — 使用注册表方式
    /// 对应 Python ToastFishLauncher.py autostart_*() 和 AutoStartManager.py
    /// </summary>
    public static class AutoStartHelper
    {
        private const string AUTOSTART_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AUTOSTART_NAME = "ToastFish";

        /// <summary>
        /// 获取启动程序的完整路径（作为注册表值）
        /// v3.0 已移除 Python 依赖，直接返回自身 exe 路径
        /// </summary>
        private static string GetLaunchPath()
        {
            string exePath = Assembly.GetEntryAssembly()?.Location
                ?? Assembly.GetExecutingAssembly().Location;
            return $"\"{exePath}\"";
        }

        /// <summary>
        /// 检查自启动是否已启用（同时验证注册路径指向的文件是否存在）
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AUTOSTART_KEY, false))
                {
                    if (key == null) return false;
                    var value = key.GetValue(AUTOSTART_NAME)?.ToString();
                    if (string.IsNullOrEmpty(value))
                        return false;

                    // value 格式为 "C:\...\ToastFish.exe"（单参数）或旧版 "pythonw.exe" "launcher.py"
                    // 提取第一个被引号包裹的路径并验证文件是否存在
                    int firstQuote = value.IndexOf('"');
                    int secondQuote = value.IndexOf('"', firstQuote + 1);
                    if (firstQuote >= 0 && secondQuote > firstQuote)
                    {
                        string path = value.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                        return File.Exists(path);
                    }
                    // 无引号的纯路径
                    return File.Exists(value.Trim());
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取当前注册的启动路径
        /// </summary>
        public static string GetCurrentPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AUTOSTART_KEY, false))
                {
                    if (key == null) return "";
                    return key.GetValue(AUTOSTART_NAME)?.ToString() ?? "";
                }
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 启用开机自启动
        /// </summary>
        public static bool Enable()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AUTOSTART_KEY, true))
                {
                    if (key == null) return false;
                    key.SetValue(AUTOSTART_NAME, GetLaunchPath());
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 禁用开机自启动
        /// </summary>
        public static bool Disable()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AUTOSTART_KEY, true))
                {
                    if (key == null) return false;
                    try
                    {
                        key.DeleteValue(AUTOSTART_NAME, false);
                    }
                    catch { /* 值不存在 */ }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // ===== 保留原 CreateShortcut 方法（兼容） =====
        /// <summary>
        /// 启动文件夹快捷方式方式（不推荐，保留用于兼容）
        /// </summary>
        public static void CreateShortcut(string lnkFilePath)
        {
            if (File.Exists(lnkFilePath))
            {
                File.Delete(lnkFilePath);
                return;
            }
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            var shortcut = shell.CreateShortcut(lnkFilePath);
            shortcut.TargetPath = Assembly.GetEntryAssembly().Location;
            shortcut.WorkingDirectory = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
            shortcut.Save();
        }
    }
}
