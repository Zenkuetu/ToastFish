using System.IO;
using System.Reflection;

namespace ToastFish
{
    /// <summary>
    /// 路径工具（2026-10-08 抽取）。
    ///
    /// 背景：CLAUDE.md 约束 #9 规定「禁止使用 Directory.GetCurrentDirectory()，
    /// 改用 Assembly.Location 拼 exe 目录下的绝对路径」（开机自启时工作目录是 System32），
    /// 但这个拼法原先在 8 个文件、19 处各写了一遍。现统一到 ExeDir。
    ///
    /// 命名空间刻意放在根命名空间 ToastFish，这样 ToastFish.Model.* / ToastFish.View
    /// 等子命名空间无需 using 即可直接使用（C# 会向外层命名空间查找）。
    /// </summary>
    internal static class PathHelper
    {
        private static string _exeDir;

        /// <summary>主程序 exe 所在目录的绝对路径（首次访问后缓存）</summary>
        public static string ExeDir
        {
            get
            {
                if (_exeDir == null)
                {
                    // ⚠️ 此处必须保留原始调用，不能替换为 PathHelper.ExeDir（会自我递归 → 栈溢出）
                    _exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                }
                return _exeDir;
            }
        }
    }
}
