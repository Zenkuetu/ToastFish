using System;
using System.Windows;
using System.Windows.Media;
using ToastFish.Model.StartWithWindows;

namespace ToastFish.View
{
    /// <summary>
    /// 自启动设置面板
    /// 对应 Python AutoStartManager.py
    /// </summary>
    public partial class AutoStartWindow : Window
    {
        public AutoStartWindow()
        {
            InitializeComponent();
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            bool enabled = AutoStartHelper.IsEnabled();
            string path = AutoStartHelper.GetCurrentPath();

            if (enabled)
            {
                StatusText.Text = "已启用";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                ToggleButton.Content = "关闭开机自启动";
                PathText.Text = $"启动路径: {path}";
            }
            else
            {
                StatusText.Text = "未启用";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xBD));
                ToggleButton.Content = "开启开机自启动";
                PathText.Text = "";
            }
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = AutoStartHelper.IsEnabled();
            if (enabled)
            {
                AutoStartHelper.Disable();
            }
            else
            {
                AutoStartHelper.Enable();
            }

            // 短暂显示 "已生效"
            string orig = ToggleButton.Content.ToString();
            ToggleButton.Content = "✓ 已生效";
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                RefreshStatus();
            };
            timer.Start();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
