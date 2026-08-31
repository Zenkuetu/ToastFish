using System;
using System.Windows;
using ToastFish.Model.StudyLog;

namespace ToastFish.View
{
    /// <summary>
    /// 学习报告面板
    /// 对应 Python ToastFishLauncher.py show_stats_panel()
    /// </summary>
    public partial class StatsWindow : Window
    {
        public StatsWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 加载并显示统计数据
        /// db 连接由调用方管理生命周期
        /// </summary>
        public void LoadStats(System.Data.SQLite.SQLiteConnection db, int updatedCount,
            int easyCount, int goodCount, int hardCount, int againCount)
        {
            var stats = StudyLogManager.ComputeTodayStats(db, updatedCount,
                easyCount, goodCount, hardCount, againCount);

            // 大数字
            TotalWordsText.Text = $"{stats.Total} 词";

            // 得分分布条形图
            int barMax = Math.Max(1, stats.Total);
            double barAreaWidth = 200;

            UpdateBar(EasyBar, EasyText, stats.Easy, barMax, barAreaWidth);
            UpdateBar(GoodBar, GoodText, stats.Good, barMax, barAreaWidth);
            UpdateBar(HardBar, HardText, stats.Hard, barMax, barAreaWidth);
            UpdateBar(AgainBar, AgainText, stats.Again, barMax, barAreaWidth);

            // 进度
            int pct = stats.WordTotal > 0 ? 100 * stats.LearnedTotal / stats.WordTotal : 0;
            ProgressText.Text = $"总进度: {stats.BookName}  {stats.LearnedTotal}/{stats.WordTotal} ({pct}%)";

            // 连续天数
            StreakText.Text = $"连续学习: {stats.Streak} 天";
        }

        private void UpdateBar(System.Windows.Controls.Border bar, System.Windows.Controls.TextBlock text,
            int count, int max, double areaWidth)
        {
            double ratio = (double)count / max;
            bar.Width = Math.Max(ratio > 0 ? ratio * areaWidth : 1, 1);
            int pct = max > 0 ? 100 * count / max : 0;
            text.Text = $"{count} ({pct}%)";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
