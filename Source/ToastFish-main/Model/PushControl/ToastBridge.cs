using System;
using System.Threading.Tasks;
using ToastFish.View;

namespace ToastFish.Model.PushControl
{
    /// <summary>
    /// Toast 通知桥接层 —— 用 WPF NotificationForm 替代不可用的 UWP Toast API
    /// </summary>
    public static class ToastBridge
    {
        /// <summary>
        /// 显示纯文本通知（无按钮，自动消失）
        /// </summary>
        public static void ShowMessage(string title, string message, int autoDismissMs = 5000)
        {
            NotificationForm.ShowMessage(title, message, autoDismissMs);
        }

        /// <summary>
        /// 显示带按钮的交互式通知，返回用户点击的按钮索引（0-based），超时返回 -1
        /// </summary>
        public static Task<int> ShowInteractive(string title, string message,
            string[] buttonLabels, int timeoutMs = 30000)
        {
            return NotificationForm.ShowWithButtons(title, message, buttonLabels, timeoutMs);
        }
    }
}
