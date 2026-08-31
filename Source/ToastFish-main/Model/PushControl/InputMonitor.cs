using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToastFish.Model.PushControl
{
    /// <summary>
    /// 用户输入监控 —— 通过 Win32 SetWindowsHookEx 注册低级键盘/鼠标钩子
    /// 每次键盘按键或鼠标点击时触发回调，用于惊喜复习的随机触发
    ///
    /// 性能：钩子回调仅做事件类型判断（O(1)），无内存分配，开销可忽略。
    /// 鼠标移动事件（WM_MOUSEMOVE）被主动过滤，不会触发回调。
    /// </summary>
    public static class InputMonitor
    {
        #region P/Invoke

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL    = 14;

        private const int WM_KEYDOWN     = 0x0100;
        private const int WM_SYSKEYDOWN  = 0x0104;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;

        private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        #endregion

        // 委托必须存为静态字段，防止 GC 回收导致崩溃
        private static readonly LowLevelHookProc _keyboardProc = KeyboardHookCallback;
        private static readonly LowLevelHookProc _mouseProc    = MouseHookCallback;

        private static IntPtr _keyboardHookId = IntPtr.Zero;
        private static IntPtr _mouseHookId    = IntPtr.Zero;

        /// <summary>每次键盘按键或鼠标点击时调用。在钩子线程上执行，必须极快返回。</summary>
        public static event Action OnInputEvent;

        private static bool _started;

        /// <summary>
        /// 安装全局键盘和鼠标钩子。需在有消息泵的线程上调用（WPF UI 线程）。
        /// 每个进程只应调用一次。
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr hMod = GetModuleHandle(curModule.ModuleName);
                // dwThreadId = 0 → 全局钩子（所有线程）
                _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
                _mouseHookId    = SetWindowsHookEx(WH_MOUSE_LL,    _mouseProc,    hMod, 0);
            }

            Debug.WriteLine("InputMonitor: 键盘钩子=0x{0:X} 鼠标钩子=0x{1:X}",
                _keyboardHookId.ToInt64(), _mouseHookId.ToInt64());
        }

        /// <summary>
        /// 卸载钩子。应在程序退出时调用。
        /// </summary>
        public static void Stop()
        {
            if (_keyboardHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
            }
            if (_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
            _started = false;
            Debug.WriteLine("InputMonitor: 钩子已卸载");
        }

        /// <summary>
        /// 键盘钩子回调 —— 仅 KeyDown 事件（过滤 KeyUp）
        /// </summary>
        private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    OnInputEvent?.Invoke();
                }
            }
            return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        /// <summary>
        /// 鼠标钩子回调 —— 仅点击事件（过滤移动/滚轮）
        /// </summary>
        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                {
                    OnInputEvent?.Invoke();
                }
            }
            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }
    }
}
