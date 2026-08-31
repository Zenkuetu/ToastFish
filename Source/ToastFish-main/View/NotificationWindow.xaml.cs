using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ToastFish.View
{
    public partial class NotificationWindow : Window
    {
        private TaskCompletionSource<int> _tcs;
        private DispatcherTimer _dismissTimer;
        private static NotificationWindow _current;

        public NotificationWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 在显示前定位到屏幕右下角
        /// </summary>
        private void PositionAtBottomRight()
        {
            var wa = System.Windows.SystemParameters.WorkArea;
            this.Left = wa.Right - this.Width - 20;
            this.Top = wa.Bottom - this.Height - 20;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PositionAtBottomRight();
        }

        /// <summary>
        /// 显示文本通知（无按钮，自动消失）
        /// </summary>
        private static void Log(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(
                            System.Reflection.Assembly.GetExecutingAssembly().Location),
                        "notify_debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\n");
            }
            catch { }
        }

        public static void ShowMessage(string title, string message, int autoDismissMs = 5000)
        {
            var dispatcher = ToastFish.Model.PushControl.PushWords.UIDispatcher;
            Log($"ShowMessage: dispatcher={(dispatcher!=null)}, msg={message.Substring(0,Math.Min(30,message.Length))}");
            if (dispatcher == null) return;

            dispatcher.Invoke(() =>
            {
                Log("ShowMessage: in dispatcher, creating window...");
                _current?.Close();
                var win = new NotificationWindow();
                _current = win;
                win.Closed += (s, e) => { if (_current == win) _current = null; };
                win.TitleText.Text = title;
                win.BodyText.Text = message;
                win.PositionAtBottomRight();
                Log($"ShowMessage: position=({win.Left},{win.Top})");
                win._dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(autoDismissMs) };
                win._dismissTimer.Tick += (s, e) => { win._dismissTimer.Stop(); win.Close(); };
                win.Show();
                win.Activate();
                Log("ShowMessage: Show() done, window visible");
                win._dismissTimer.Start();
            });
        }

        /// <summary>
        /// 显示带按钮的通知，返回用户选择（0-based），超时返回 -1
        /// </summary>
        public static Task<int> ShowWithButtons(string title, string message,
            string[] buttonLabels, int timeoutMs = 60000)
        {
            var tcs = new TaskCompletionSource<int>();
            var dispatcher = ToastFish.Model.PushControl.PushWords.UIDispatcher;
            Log($"ShowWithButtons: dispatcher={(dispatcher!=null)}, btns={buttonLabels.Length}");
            if (dispatcher == null) return Task.FromResult(-1);

            dispatcher.Invoke(() =>
            {
                Log("ShowWithButtons: in dispatcher, creating window...");
                _current?.Close();
                var win = new NotificationWindow();
                _current = win;
                win.Closed += (s, e) => { if (_current == win) _current = null; };
                win._tcs = tcs;
                win.TitleText.Text = title;
                win.BodyText.Text = message;

                for (int i = 0; i < buttonLabels.Length; i++)
                {
                    var btn = new Button
                    {
                        Content = buttonLabels[i],
                        Margin = new Thickness(4, 0, 0, 0),
                        Padding = new Thickness(10, 3, 10, 3),
                        Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                        Foreground = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                        BorderThickness = new Thickness(1),
                        FontSize = 13,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Tag = i
                    };
                    btn.Click += (s2, e2) =>
                    {
                        tcs.TrySetResult((int)((Button)s2).Tag);
                        win.Close();
                    };
                    win.ButtonPanel.Children.Add(btn);
                }

                win._dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(timeoutMs) };
                win._dismissTimer.Tick += (s3, e3) =>
                {
                    win._dismissTimer.Stop();
                    tcs.TrySetResult(-1);
                    win.Close();
                };

                win.PositionAtBottomRight();
                win.Show();
                win.Activate();
                Log($"ShowWithButtons: Show() done at ({win.Left},{win.Top})");
                win._dismissTimer.Start();
            });
            Log("ShowWithButtons: returning Task");
            return tcs.Task;
        }

        protected override void OnClosed(EventArgs e)
        {
            _dismissTimer?.Stop();
            if (!(_tcs?.Task.IsCompleted ?? true))
                _tcs?.TrySetResult(-1);
            base.OnClosed(e);
        }
    }
}
