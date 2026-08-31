using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ToastFish.Model.PushControl
{
    /// <summary>
    /// 手柄输入监控 —— 通过 XInput API 轮询手柄状态，检测按钮新按下事件。
    /// 用于惊喜复习系统的手柄触发。
    ///
    /// XInput 仅支持 Xbox 兼容手柄。Windows 8+ 自带 xinput1_4.dll。
    /// 手柄未连接时 XInputGetState 返回非 0，轮询静默跳过。
    /// 轮询间隔 50ms（20Hz），CPU 开销可忽略。
    /// </summary>
    public static class GamepadMonitor
    {
        #region XInput P/Invoke

        // XInputGetState 返回值
        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_DEVICE_NOT_CONNECTED = 0x048F;

        // 按钮标志（wButtons 位掩码）
        private const ushort XINPUT_GAMEPAD_DPAD_UP        = 0x0001;
        private const ushort XINPUT_GAMEPAD_DPAD_DOWN      = 0x0002;
        private const ushort XINPUT_GAMEPAD_DPAD_LEFT      = 0x0004;
        private const ushort XINPUT_GAMEPAD_DPAD_RIGHT     = 0x0008;
        private const ushort XINPUT_GAMEPAD_START          = 0x0010;
        private const ushort XINPUT_GAMEPAD_BACK           = 0x0020;
        private const ushort XINPUT_GAMEPAD_LEFT_THUMB     = 0x0040;
        private const ushort XINPUT_GAMEPAD_RIGHT_THUMB    = 0x0080;
        private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER  = 0x0100;
        private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
        private const ushort XINPUT_GAMEPAD_A              = 0x1000;
        private const ushort XINPUT_GAMEPAD_B              = 0x2000;
        private const ushort XINPUT_GAMEPAD_X              = 0x4000;
        private const ushort XINPUT_GAMEPAD_Y              = 0x8000;

        /// <summary>所有监控的按钮位掩码（14 个数字按钮，不含摇杆/扳机模拟量）</summary>
        private const ushort ALL_BUTTONS =
            XINPUT_GAMEPAD_DPAD_UP | XINPUT_GAMEPAD_DPAD_DOWN |
            XINPUT_GAMEPAD_DPAD_LEFT | XINPUT_GAMEPAD_DPAD_RIGHT |
            XINPUT_GAMEPAD_START | XINPUT_GAMEPAD_BACK |
            XINPUT_GAMEPAD_LEFT_THUMB | XINPUT_GAMEPAD_RIGHT_THUMB |
            XINPUT_GAMEPAD_LEFT_SHOULDER | XINPUT_GAMEPAD_RIGHT_SHOULDER |
            XINPUT_GAMEPAD_A | XINPUT_GAMEPAD_B | XINPUT_GAMEPAD_X | XINPUT_GAMEPAD_Y;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", SetLastError = true)]
        private static extern uint XInputGetState_1_4(uint dwUserIndex, ref XINPUT_STATE pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState", SetLastError = true)]
        private static extern uint XInputGetState_9_1(uint dwUserIndex, ref XINPUT_STATE pState);

        #endregion

        /// <summary>手柄按钮按下时触发。在 Timer 线程上执行，回调必须极快返回。</summary>
        public static event Action OnInputEvent;

        private static Timer _pollTimer;
        private static ushort _prevButtons;
        private static bool _useXInput9;  // 回退到 xinput9_1_0.dll

        private const int POLL_INTERVAL_MS = 50;        // 20 Hz
        private const int POLL_DUE_TIME_MS = 200;       // 首次延迟稍长，避开程序启动尖峰

        private static bool _started;

        /// <summary>
        /// 启动手柄轮询。应在程序初始化时调用一次。
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            _prevButtons = 0;
            _useXInput9 = false;
            _pollTimer = new Timer(PollCallback, null, POLL_DUE_TIME_MS, POLL_INTERVAL_MS);

            Debug.WriteLine("GamepadMonitor: 已启动, 轮询间隔={0}ms", POLL_INTERVAL_MS);
        }

        /// <summary>
        /// 停止轮询。应在程序退出时调用。
        /// </summary>
        public static void Stop()
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            _started = false;
            Debug.WriteLine("GamepadMonitor: 已停止");
        }

        /// <summary>
        /// 定时器回调（线程池线程）—— 读取手柄状态，检测新按下事件
        /// </summary>
        private static void PollCallback(object state)
        {
            try
            {
                XINPUT_STATE xstate = new XINPUT_STATE();
                uint result;

                // 优先使用 xinput1_4（Win8+），失败回退到 xinput9_1_0（WinXP+）
                if (!_useXInput9)
                {
                    result = XInputGetState_1_4(0, ref xstate);
                    // 0x8007007E = ERROR_MOD_NOT_FOUND（DLL 不存在）
                    if (result == 0x8007007E)
                    {
                        _useXInput9 = true;
                        result = XInputGetState_9_1(0, ref xstate);
                    }
                }
                else
                {
                    result = XInputGetState_9_1(0, ref xstate);
                }

                if (result == ERROR_SUCCESS)
                {
                    // 手柄已连接：检测本次轮询新按下的按钮
                    ushort currentButtons = (ushort)(xstate.Gamepad.wButtons & ALL_BUTTONS);
                    ushort previous = _prevButtons;
                    _prevButtons = currentButtons;

                    ushort newPresses = (ushort)(currentButtons & ~previous);
                    if (newPresses != 0)
                    {
                        OnInputEvent?.Invoke();
                    }
                }
                else
                {
                    // 手柄未连接或错误 → 重置状态，防止重连时误判
                    _prevButtons = 0;
                }
            }
            catch (Exception ex)
            {
                // DllNotFoundException / EntryPointNotFoundException 等
                Debug.WriteLine("GamepadMonitor: PollCallback 异常: " + ex.Message);
                // 停止轮询，避免反复抛异常
                _pollTimer?.Dispose();
                _pollTimer = null;
            }
        }
    }
}
