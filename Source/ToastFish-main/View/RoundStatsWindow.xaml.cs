using System.Windows;

namespace ToastFish.View
{
    /// <summary>
    /// 每轮学习结束后的统计面板
    /// 显示本轮新学/复习分布，简洁有意义
    /// </summary>
    public partial class RoundStatsWindow : Window
    {
        public RoundStatsWindow()
        {
            InitializeComponent();
        }

        public void SetStats(int totalWords, int newWords, int reviewWords)
        {
            TotalText.Text = $"本轮完成 {totalWords} 词";
            NewText.Text = $"新学 {newWords}";
            ReviewText.Text = $"复习 {reviewWords}";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
