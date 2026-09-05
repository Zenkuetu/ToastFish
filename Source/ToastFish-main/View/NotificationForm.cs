using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ToastFish.Model.Ai;
using ToastFish.Model.SqliteControl;

namespace ToastFish.View
{
    /// <summary>
    /// WinForms 通知弹窗
    /// </summary>
    public static class NotificationForm
    {
        private static Form _current;

        // 弹窗状态跟踪（用于隐藏/显示切换）
        private class PopupState
        {
            public System.Windows.Forms.Timer Timer;
            public int TotalMs;            // 原始超时（毫秒）
            public DateTime StartedAt;     // Timer 启动时刻
            public int PausedRemainingMs;  // 暂停时剩余毫秒数（-1 = 未暂停）
        }

        private static bool _isHidden = false;

        // 弹窗超时常量（毫秒）
        public const int TIMEOUT_WORD  = 180000;  // 单词弹窗：3分钟
        public const int TIMEOUT_QUIZ  = 300000;  // 测验弹窗：5分钟
        public const int TIMEOUT_MSG   = 5000;    // 简短通知：5秒
        public const int TIMEOUT_DIALOG = 180000; // 交互弹窗：3分钟
        public const int TIMEOUT_SURPRISE = 60000; // 惊喜复习弹窗：1分钟
        public const int TIMEOUT_PREVIEW = 3000;   // 答错预览弹窗：3秒
        public const int TIMEOUT_READING = 0;      // 学后微阅读/AI短文弹窗：0 = 无时限（阅读不限时）

        // ===== 阅读/短文/完形弹窗布局常量（2026-08-08 布局机制重设计，仅服务阅读家族，不影响其他弹窗）=====
        private const double MAX_SCREEN_RATIO_READING = 0.75; // 统一窗口高度上界 = 屏幕工作区 75%
        private const int LAYOUT_GAP = 10;                    // 段间间距
        private const int PAD_TOP = 14;                       // 顶部内边距
        private const int PAD_BOTTOM = 28;                    // 底部内边距
        private const int MIN_FORM_H = 180;                   // 结构下限（短内容窗口最小高度，替代旧 260）
        private const int CAND_PANEL_H = 130;                 // 15选10 候选词面板固定高

        /// <summary>
        /// 单词弹窗 —— 将 ‖ 分隔的各字段用不同样式渲染
        /// 字段索引: 0=单词, 1=音标, 2=词性+释义, 3=例句, 4=例句翻译, 5=词组, 6=词组翻译, 7=空, 8=SM2状态
        /// </summary>
        // 字体缓存，避免重复 new Font
        private static readonly Font FONT_WORD  = new Font("Microsoft YaHei UI", 15, FontStyle.Bold);
        private static readonly Font FONT_POS   = new Font("Microsoft YaHei UI", 11, FontStyle.Regular);
        private static readonly Font FONT_EX    = new Font("Microsoft YaHei UI", 10, FontStyle.Italic);
        private static readonly Font FONT_EXCN  = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);
        private static readonly Font FONT_STAT  = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);
        private static readonly Font FONT_BTN   = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);

        private static int MeasureH(string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, font,
                new Size(maxWidth, 9999),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        }

        /// <summary>当前屏幕工作区（非 DPI-aware 下与所有测量同处一个逻辑像素坐标系，125%/150% 比例自洽）</summary>
        private static Rectangle ScreenArea() => Screen.PrimaryScreen.WorkingArea;

        /// <summary>阅读/短文/完形弹窗统一窗口高度上界 = 屏幕工作区 75%</summary>
        private static int CapH() => (int)(ScreenArea().Height * MAX_SCREEN_RATIO_READING);

        /// <summary>
        /// 同步获取 RichTextBox 的真实内容高度（需句柄已创建）。
        /// ContentsResized 是异步事件（仅在句柄创建后触发），交互期内容变化时用它同步取高更可靠。
        /// </summary>
        private static int RealRtbHeight(RichTextBox rtb)
        {
            int n = rtb.TextLength;
            if (n == 0) return 0;
            Point p = rtb.GetPositionFromCharIndex(n - 1);
            return p.Y + rtb.Font.Height + 2;
        }

        public static void ShowWordPopup(string data,
            string[] buttonLabels, Action<int> callback, int timeoutMs = TIMEOUT_WORD)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(-1); return; }

            // BeginInvoke 异步排队，不阻塞当前线程（避免启动时短暂未响应）
            dispatcher.BeginInvoke(new Action(() =>
            {
                _current?.Close();
                _current?.Dispose();

                string[] fields = data.Split(new[] { "‖" }, StringSplitOptions.None);
                string word      = fields.Length > 0 ? fields[0].Trim() : "";
                string phoneme   = fields.Length > 1 ? fields[1].Trim() : "";
                string posTran   = fields.Length > 2 ? fields[2].Trim() : "";
                string sentence  = fields.Length > 3 ? fields[3].Trim() : "";
                string sentCN    = fields.Length > 4 ? fields[4].Trim() : "";
                string phrase    = fields.Length > 5 ? fields[5].Trim() : "";
                string phraseCN  = fields.Length > 6 ? fields[6].Trim() : "";
                string status    = fields.Length > 8 ? fields[8].Trim() : "";

                int formW = 540, pad = 20;
                int cw = formW - pad * 2;   // 内容宽度
                int ciw = cw - 8;            // 缩进宽度

                // === 1) 用 MeasureText 预计算每块的像素高度 ===
                string wordLine = word;
                if (!string.IsNullOrEmpty(phoneme)) wordLine += "  " + phoneme;
                int hWord   = MeasureH(wordLine, FONT_WORD, cw);
                int hPos    = MeasureH(posTran, FONT_POS, cw);
                int hSent   = string.IsNullOrEmpty(sentence) ? 0 : MeasureH("「" + sentence + "」", FONT_EX, ciw);
                int hSentCN = string.IsNullOrEmpty(sentCN) ? 0 : MeasureH(sentCN, FONT_EXCN, ciw);
                int hPhr    = string.IsNullOrEmpty(phrase) ? 0 : MeasureH("◆ " + phrase, FONT_EX, ciw);
                int hPhrCN  = string.IsNullOrEmpty(phraseCN) ? 0 : MeasureH(phraseCN, FONT_EXCN, ciw);
                int hStat   = string.IsNullOrEmpty(status) ? 0 : MeasureH(status, FONT_STAT, cw);

                // 段间固定间距（10px），只在两块都非空时计入
                const int GAP = 10;
                bool hasPos   = !string.IsNullOrEmpty(posTran);
                bool hasSent  = !string.IsNullOrEmpty(sentence);
                bool hasPhr   = !string.IsNullOrEmpty(phrase);
                bool hasStat  = !string.IsNullOrEmpty(status);
                bool hasSentCN = !string.IsNullOrEmpty(sentCN);
                bool hasPhrCN  = !string.IsNullOrEmpty(phraseCN);

                int contentH = 14 + hWord
                    + (hasPos ? GAP + hPos : 0)
                    + (hasSent ? GAP + hSent + 2 + (hasSentCN ? hSentCN : 0) : 0)
                    + (hasPhr ? GAP + hPhr + 2 + (hasPhrCN ? hPhrCN : 0) : 0)
                    + (hasStat ? GAP + hStat : 0)
                    + 8;

                int btnH = 44;
                int formH = contentH + btnH + 20;
                formH = Math.Min(formH, (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.85));
                formH = Math.Max(formH, 200);

                var form = new Form
                {
                    Text = "ToastFish",
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    Width = formW,
                    Height = formH,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.White
                };

                var wa = Screen.PrimaryScreen.WorkingArea;
                form.Left = wa.Right - form.Width - 20;
                form.Top = wa.Bottom - form.Height - 20;

                // === 逐段放置，每段结束后加固定间距 ===
                int y = 14;
                int GAP_SECTION = 10;  // 段间间距

                // 单词 + 音标
                var wlbl = NewFixedLabel(wordLine, Color.White, FONT_WORD, pad, y, cw, hWord);
                form.Controls.Add(wlbl);
                y += hWord;
                if (hPos > 0 || hSent > 0 || hPhr > 0 || hStat > 0) y += GAP_SECTION;

                // 词性 + 释义
                if (!string.IsNullOrEmpty(posTran))
                {
                    var plbl = NewFixedLabel(posTran, Color.FromArgb(130, 210, 255), FONT_POS, pad, y, cw, hPos);
                    form.Controls.Add(plbl);
                    y += hPos;
                    if (hSent > 0 || hPhr > 0 || hStat > 0) y += GAP_SECTION;
                }

                // 例句（含翻译）
                if (!string.IsNullOrEmpty(sentence))
                {
                    int x1 = pad + 8, w1 = ciw;
                    var slbl = NewFixedLabel("「" + sentence + "」", Color.FromArgb(180, 180, 180), FONT_EX, x1, y, w1, MeasureH("「" + sentence + "」", FONT_EX, w1));
                    form.Controls.Add(slbl);
                    y += slbl.Height + 2;

                    if (!string.IsNullOrEmpty(sentCN))
                    {
                        var sclbl = NewFixedLabel(sentCN, Color.FromArgb(140, 140, 140), FONT_EXCN, x1, y, w1, hSentCN);
                        form.Controls.Add(sclbl);
                        y += hSentCN;
                    }
                    if (hPhr > 0 || hStat > 0) y += GAP_SECTION;
                }

                // 词组（含翻译）
                if (!string.IsNullOrEmpty(phrase))
                {
                    int x1 = pad + 8, w1 = ciw;
                    var phlbl = NewFixedLabel("◆ " + phrase, Color.FromArgb(180, 180, 180), FONT_EX, x1, y, w1, MeasureH("◆ " + phrase, FONT_EX, w1));
                    form.Controls.Add(phlbl);
                    y += phlbl.Height + 2;

                    if (!string.IsNullOrEmpty(phraseCN))
                    {
                        var pclbl = NewFixedLabel(phraseCN, Color.FromArgb(140, 140, 140), FONT_EXCN, x1, y, w1, hPhrCN);
                        form.Controls.Add(pclbl);
                        y += hPhrCN;
                    }
                    if (hStat > 0) y += GAP_SECTION;
                }

                // SM2 状态
                if (!string.IsNullOrEmpty(status))
                {
                    var stlbl = NewFixedLabel(status, Color.FromArgb(255, 200, 60), FONT_STAT, pad, y, cw, hStat);
                    form.Controls.Add(stlbl);
                }

                // === 4) 按钮 ===
                int btnY = form.ClientSize.Height - btnH - 14;
                int totalBw = buttonLabels.Length * 110 + (buttonLabels.Length - 1) * 10;
                int startX = (form.ClientSize.Width - totalBw) / 2;
                for (int i = 0; i < buttonLabels.Length; i++)
                {
                    int idx = i;
                    var btn = new Button
                    {
                        Text = buttonLabels[i],
                        Location = new Point(startX + i * 120, btnY),
                        Size = new Size(110, btnH),
                        BackColor = Color.FromArgb(51, 51, 51),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Font = FONT_BTN,
                        Tag = idx
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
                    btn.Click += (s, e) => { form.Close(); callback(idx); };
                    form.Controls.Add(btn);
                }

                var timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                timer.Tick += (s, e) => { timer.Stop(); callback(-1); form.Close(); };
                timer.Start();
                form.Tag = new PopupState { Timer = timer, TotalMs = timeoutMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };

                form.FormClosed += (s, e) =>
                {
                    timer.Dispose();
                    if (_current == form) _current = null;
                    _isHidden = false;
                    form.Dispose();
                };

                _isHidden = false;
                _current = form;
                form.Show();
            }));
        }

        private static Label NewFixedLabel(string text, Color color, Font font, int x, int y, int w, int h)
        {
            return new Label
            {
                Text = text,
                ForeColor = color,
                Font = font,
                Location = new Point(x, y),
                Size = new Size(w, h),
                AutoSize = false
            };
        }

        /// <summary>
        /// 答错预览弹窗 —— 与 ShowWordPopup 布局相同但无互动按钮，3 秒自动消失
        /// 用于惊喜复习答错后展示正确答案
        /// </summary>
        public static void ShowWordPreviewPopup(string data, int timeoutMs = TIMEOUT_PREVIEW)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                // 不关闭当前弹窗（惊喜复习弹窗本身就是当前弹窗，由 ProcessAnswer 在回调中关闭）
                // 但如果已有预览弹窗，先关掉旧的
                _current?.Close();
                _current?.Dispose();

                string[] fields = data.Split(new[] { "‖" }, StringSplitOptions.None);
                string word      = fields.Length > 0 ? fields[0].Trim() : "";
                string phoneme   = fields.Length > 1 ? fields[1].Trim() : "";
                string posTran   = fields.Length > 2 ? fields[2].Trim() : "";
                string sentence  = fields.Length > 3 ? fields[3].Trim() : "";
                string sentCN    = fields.Length > 4 ? fields[4].Trim() : "";
                string phrase    = fields.Length > 5 ? fields[5].Trim() : "";
                string phraseCN  = fields.Length > 6 ? fields[6].Trim() : "";
                string status    = fields.Length > 8 ? fields[8].Trim() : "";

                int formW = 540, pad = 20;
                int cw = formW - pad * 2;
                int ciw = cw - 8;

                // === 预计算每块高度 ===
                string wordLine = word;
                if (!string.IsNullOrEmpty(phoneme)) wordLine += "  " + phoneme;
                int hWord   = MeasureH(wordLine, FONT_WORD, cw);
                int hPos    = MeasureH(posTran, FONT_POS, cw);
                int hSent   = string.IsNullOrEmpty(sentence) ? 0 : MeasureH("「" + sentence + "」", FONT_EX, ciw);
                int hSentCN = string.IsNullOrEmpty(sentCN) ? 0 : MeasureH(sentCN, FONT_EXCN, ciw);
                int hPhr    = string.IsNullOrEmpty(phrase) ? 0 : MeasureH("◆ " + phrase, FONT_EX, ciw);
                int hPhrCN  = string.IsNullOrEmpty(phraseCN) ? 0 : MeasureH(phraseCN, FONT_EXCN, ciw);
                int hStat   = string.IsNullOrEmpty(status) ? 0 : MeasureH(status, FONT_STAT, cw);

                const int GAP = 10;
                bool hasPos   = !string.IsNullOrEmpty(posTran);
                bool hasSent  = !string.IsNullOrEmpty(sentence);
                bool hasPhr   = !string.IsNullOrEmpty(phrase);
                bool hasStat  = !string.IsNullOrEmpty(status);
                bool hasSentCN = !string.IsNullOrEmpty(sentCN);
                bool hasPhrCN  = !string.IsNullOrEmpty(phraseCN);

                int contentH = 14 + hWord
                    + (hasPos ? GAP + hPos : 0)
                    + (hasSent ? GAP + hSent + 2 + (hasSentCN ? hSentCN : 0) : 0)
                    + (hasPhr ? GAP + hPhr + 2 + (hasPhrCN ? hPhrCN : 0) : 0)
                    + (hasStat ? GAP + hStat : 0)
                    + 8;

                // 无按钮区域，底部仅加答错提示行（~24px）
                int hintH = 24;
                int formH = contentH + hintH + 20;
                formH = Math.Min(formH, (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.85));
                formH = Math.Max(formH, 140);

                var form = new Form
                {
                    Text = "ToastFish",
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    Width = formW,
                    Height = formH,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.White
                };

                var wa = Screen.PrimaryScreen.WorkingArea;
                form.Left = wa.Right - form.Width - 20;
                form.Top = wa.Bottom - form.Height - 20;

                // === 逐段放置 ===
                int y = 14;
                const int GAP_SECTION = 10;

                // 单词 + 音标
                var wlbl = NewFixedLabel(wordLine, Color.White, FONT_WORD, pad, y, cw, hWord);
                form.Controls.Add(wlbl);
                y += hWord;
                if (hPos > 0 || hSent > 0 || hPhr > 0 || hStat > 0) y += GAP_SECTION;

                // 词性 + 释义
                if (!string.IsNullOrEmpty(posTran))
                {
                    var plbl = NewFixedLabel(posTran, Color.FromArgb(130, 210, 255), FONT_POS, pad, y, cw, hPos);
                    form.Controls.Add(plbl);
                    y += hPos;
                    if (hSent > 0 || hPhr > 0 || hStat > 0) y += GAP_SECTION;
                }

                // 例句
                if (!string.IsNullOrEmpty(sentence))
                {
                    int x1 = pad + 8, w1 = ciw;
                    var slbl = NewFixedLabel("「" + sentence + "」", Color.FromArgb(180, 180, 180), FONT_EX, x1, y, w1, MeasureH("「" + sentence + "」", FONT_EX, w1));
                    form.Controls.Add(slbl);
                    y += slbl.Height + 2;

                    if (!string.IsNullOrEmpty(sentCN))
                    {
                        var sclbl = NewFixedLabel(sentCN, Color.FromArgb(140, 140, 140), FONT_EXCN, x1, y, w1, hSentCN);
                        form.Controls.Add(sclbl);
                        y += hSentCN;
                    }
                    if (hPhr > 0 || hStat > 0) y += GAP_SECTION;
                }

                // 词组
                if (!string.IsNullOrEmpty(phrase))
                {
                    int x1 = pad + 8, w1 = ciw;
                    var phlbl = NewFixedLabel("◆ " + phrase, Color.FromArgb(180, 180, 180), FONT_EX, x1, y, w1, MeasureH("◆ " + phrase, FONT_EX, w1));
                    form.Controls.Add(phlbl);
                    y += phlbl.Height + 2;

                    if (!string.IsNullOrEmpty(phraseCN))
                    {
                        var pclbl = NewFixedLabel(phraseCN, Color.FromArgb(140, 140, 140), FONT_EXCN, x1, y, w1, hPhrCN);
                        form.Controls.Add(pclbl);
                        y += hPhrCN;
                    }
                    if (hStat > 0) y += GAP_SECTION;
                }

                // SM2 状态
                if (!string.IsNullOrEmpty(status))
                {
                    var stlbl = NewFixedLabel(status, Color.FromArgb(255, 200, 60), FONT_STAT, pad, y, cw, hStat);
                    form.Controls.Add(stlbl);
                }

                // === 底部答错提示 ===
                int hintY = form.ClientSize.Height - hintH - 10;
                var hintLbl = NewFixedLabel("✗ 回答错误", Color.FromArgb(255, 100, 100), FONT_BTN, pad, hintY, cw, hintH);
                form.Controls.Add(hintLbl);

                // 3 秒自动关闭
                var timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                timer.Tick += (s, e) => { timer.Stop(); form.Close(); };
                timer.Start();

                form.FormClosed += (s, e) =>
                {
                    timer.Dispose();
                    if (_current == form) _current = null;
                    _isHidden = false;
                    form.Dispose();
                };

                _isHidden = false;
                _current = form;
                form.Show();
            }));
        }

        /// <summary>
        /// 非阻塞显示交互式弹窗（纯文本版 — 向后兼容）
        /// </summary>
        public static void ShowWithButtonsAsync(string title, string message,
            string[] buttonLabels, Action<int> callback, int timeoutMs = TIMEOUT_DIALOG)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(-1); return; }

            dispatcher.BeginInvoke(new Action(() =>
            {
                // 清理和创建窗口逻辑同上，用 Async 路径不再详述
                // 此方法已废弃，保留仅为兼容
                callback(-1);
            }));
        }

        public static void ShowMessage(string title, string message, int autoDismissMs = TIMEOUT_MSG)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                _current?.Close();
                _current?.Dispose();

                var form = new Form
                {
                    Text = title,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    Width = 420,
                    Height = 160,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 12)
                };

                // 定位右下角
                var wa = Screen.PrimaryScreen.WorkingArea;
                form.Left = wa.Right - form.Width - 16;
                form.Top = wa.Bottom - form.Height - 16;

                var titleLabel = new Label
                {
                    Text = title,
                    ForeColor = Color.FromArgb(136, 136, 136),
                    Font = new Font("Microsoft YaHei UI", 9),
                    Location = new Point(16, 12),
                    AutoSize = true
                };
                form.Controls.Add(titleLabel);

                var bodyLabel = new Label
                {
                    Text = message,
                    ForeColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 13),
                    Location = new Point(16, 32),
                    MaximumSize = new Size(380, 0),
                    AutoSize = true
                };
                form.Controls.Add(bodyLabel);

                var timer = new System.Windows.Forms.Timer { Interval = autoDismissMs };
                timer.Tick += (s, e) => { timer.Stop(); form.Close(); };
                timer.Start();
                form.Tag = new PopupState { Timer = timer, TotalMs = autoDismissMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };

                form.FormClosed += (s, e) => { if (_current == form) _current = null; _isHidden = false; form.Dispose(); timer.Dispose(); };

                _isHidden = false;
                _current = form;
                form.Show();
            }));
        }

        public static Task<int> ShowWithButtons(string title, string message,
            string[] buttonLabels, int timeoutMs = TIMEOUT_DIALOG)
        {
            var tcs = new TaskCompletionSource<int>();
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) return Task.FromResult(-1);

            dispatcher.BeginInvoke(new Action(() =>
            {
                _current?.Close();
                _current?.Dispose();

                var form = new Form
                {
                    Text = title,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    Width = 440,
                    Height = 200,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 12)
                };

                var wa = Screen.PrimaryScreen.WorkingArea;
                form.Left = wa.Right - form.Width - 16;
                form.Top = wa.Bottom - form.Height - 16;

                var titleLabel = new Label
                {
                    Text = title,
                    ForeColor = Color.FromArgb(136, 136, 136),
                    Font = new Font("Microsoft YaHei UI", 9),
                    Location = new Point(16, 12),
                    AutoSize = true
                };
                form.Controls.Add(titleLabel);

                var bodyLabel = new Label
                {
                    Text = message,
                    ForeColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 13),
                    Location = new Point(16, 32),
                    MaximumSize = new Size(400, 60),
                    AutoSize = true
                };
                form.Controls.Add(bodyLabel);

                // Buttons
                int btnX = 280 - (buttonLabels.Length - 1) * 100;
                for (int i = 0; i < buttonLabels.Length; i++)
                {
                    int idx = i;
                    var btn = new Button
                    {
                        Text = buttonLabels[i],
                        Location = new Point(btnX + i * 105, 120),
                        Size = new Size(95, 30),
                        BackColor = Color.FromArgb(51, 51, 51),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Font = new Font("Microsoft YaHei UI", 10),
                        Tag = idx
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
                    btn.Click += (s, e) =>
                    {
                        tcs.TrySetResult((int)((Button)s).Tag);
                        form.Close();
                    };
                    form.Controls.Add(btn);
                }

                var timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                timer.Tick += (s, e) => { timer.Stop(); tcs.TrySetResult(-1); form.Close(); };
                timer.Start();
                form.Tag = new PopupState { Timer = timer, TotalMs = timeoutMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };

                // 快捷键：复用全局热键 ALT+1/2/3/4 → 按钮 0/1/2/3（2026-09-01 新增）
                IDisposable hotkeySub = null;
                try
                {
                    hotkeySub = Model.PushControl.PushWords.HotKeytObservable.Subscribe(key =>
                    {
                        int sel = -1;
                        if (key == "1") sel = 0;
                        else if (key == "2") sel = 1;
                        else if (key == "3") sel = 2;
                        else if (key == "4") sel = 3;
                        if (sel >= 0 && sel < buttonLabels.Length)
                        {
                            tcs.TrySetResult(sel);
                            form.Close();
                        }
                    });
                }
                catch { }

                form.FormClosed += (s, e) => { hotkeySub?.Dispose(); if (_current == form) _current = null; _isHidden = false; form.Dispose(); timer.Dispose(); };

                _isHidden = false;
                _current = form;
                form.Show();
            }));
            return tcs.Task;
        }

        /// <summary>
        /// 复盘测验弹窗 —— 标题 + 题目 + 选项按钮（3 或 4 个）
        /// 点击按钮时回调 callback(idx)，超时返回 -1
        /// </summary>
        public static void ShowQuizPopup(string title, string question,
            string[] choices, Action<int> callback, int timeoutMs = TIMEOUT_QUIZ)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(-1); return; }

            dispatcher.BeginInvoke(new Action(() =>
            {
                _current?.Close();
                _current?.Dispose();

                int formW = 500, padX = 20, padTop = 14;
                int cw = formW - padX * 2;

                // 预计算题目文本高度
                int hTitle = string.IsNullOrEmpty(title) ? 0 :
                    TextRenderer.MeasureText(title, FONT_BTN,
                        new Size(cw, 9999),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                int hQuestion = string.IsNullOrEmpty(question) ? 0 :
                    TextRenderer.MeasureText(question, FONT_POS,
                        new Size(cw, 9999),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;

                // 按钮区域：高度按文字实测自适应（长释义/长选项自动换行完整显示），最小 42px
                // 文字可用宽 = 按钮宽 - 24（左 Padding 12 + 右缓冲 12）
                int btnGap = 8;
                int btnW = cw;
                int[] btnHs = new int[choices.Length];
                int btnAreaH = (choices.Length - 1) * btnGap;
                for (int i = 0; i < choices.Length; i++)
                {
                    int th = string.IsNullOrEmpty(choices[i]) ? 0 :
                        TextRenderer.MeasureText(choices[i], FONT_BTN,
                            new Size(btnW - 24, 9999),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    btnHs[i] = Math.Max(42, th + 14);
                    btnAreaH += btnHs[i];
                }

                int contentH = padTop
                    + (hTitle > 0 ? hTitle + 6 : 0)
                    + (hQuestion > 0 ? hQuestion + 14 : 0)
                    + btnAreaH
                    + 20;

                int formH = contentH;
                bool needsScroll = contentH > (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.85);
                formH = Math.Min(formH, (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.85));
                formH = Math.Max(formH, 180);

                var form = new Form
                {
                    Text = "ToastFish",
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    Width = formW,
                    Height = formH,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.White
                };

                // 内容超出屏高时启用滚动，选项不会被截断
                if (needsScroll)
                {
                    form.AutoScroll = true;
                    form.AutoScrollMinSize = new Size(formW - SystemInformation.VerticalScrollBarWidth, contentH);
                }

                var wa = Screen.PrimaryScreen.WorkingArea;
                form.Left = wa.Right - form.Width - 20;
                form.Top = wa.Bottom - form.Height - 20;

                int y = padTop;

                // 标题
                if (!string.IsNullOrEmpty(title))
                {
                    var tlbl = NewFixedLabel(title,
                        Color.FromArgb(136, 136, 136), FONT_BTN, padX, y, cw, hTitle);
                    form.Controls.Add(tlbl);
                    y += hTitle + 6;
                }

                // 题目正文
                if (!string.IsNullOrEmpty(question))
                {
                    var qlbl = NewFixedLabel(question,
                        Color.White, FONT_POS, padX, y, cw, hQuestion);
                    form.Controls.Add(qlbl);
                    y += hQuestion + 14;
                }

                // 选项按钮（垂直堆叠，高度自适应）
                for (int i = 0; i < choices.Length; i++)
                {
                    int idx = i;
                    var btn = new Button
                    {
                        Text = choices[i],
                        Location = new Point(padX, y),
                        Size = new Size(btnW, btnHs[i]),
                        BackColor = Color.FromArgb(51, 51, 51),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Font = FONT_BTN,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Padding = new Padding(12, 0, 0, 0),
                        Tag = idx
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
                    btn.Click += (s, e) => { form.Close(); callback(idx); };
                    form.Controls.Add(btn);
                    y += btnHs[i] + btnGap;
                }

                var timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                timer.Tick += (s, e) => { timer.Stop(); callback(-1); form.Close(); };
                timer.Start();
                form.Tag = new PopupState { Timer = timer, TotalMs = timeoutMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };

                form.FormClosed += (s, e) =>
                {
                    timer.Dispose();
                    if (_current == form) _current = null;
                    _isHidden = false;
                    form.Dispose();
                };

                _isHidden = false;
                _current = form;
                form.Show();
                form.BringToFront();   // 重新抢回置顶带顶部（其他 TopMost 窗口可能在上）
                // 关闭前一个弹窗时激活权会异步转交给其他窗口（如视频小窗），可能在 BringToFront
                // 之后才发生并把该窗口抬到上面；排队一次置顶重申，在激活转交完成后执行
                form.BeginInvoke(new Action(() => { form.TopMost = true; form.BringToFront(); }));
            }));
        }

        /// <summary>
        /// 切换当前弹窗的显示/隐藏状态（ALT+H 热键入口）
        /// 隐藏时暂停计时器并记录剩余时间，显示时恢复计时并刷新剩余时间
        /// </summary>

        /// <summary>给阅读 RichTextBox 挂「点击任意单词查有道词典」：悬停单词变手型，点击跳转浏览器。</summary>
        private static void AttachWordLookup(RichTextBox rtb)
        {
            rtb.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                string w = GetWordAtPosition(rtb, e.Location);
                if (!string.IsNullOrEmpty(w)) OpenYoudaoDict(w);
            };
            rtb.MouseMove += (s, e) =>
            {
                string w = GetWordAtPosition(rtb, e.Location);
                rtb.Cursor = string.IsNullOrEmpty(w) ? Cursors.IBeam : Cursors.Hand;
            };
        }

        /// <summary>从 RichTextBox 的点击位置反查完整英文单词；点击处非字母返回 null。</summary>
        private static string GetWordAtPosition(RichTextBox rtb, Point location)
        {
            try
            {
                int idx = rtb.GetCharIndexFromPosition(location);
                if (idx < 0 || idx >= rtb.TextLength) return null;
                if (!char.IsLetter(rtb.Text[idx])) return null;
                int start = idx, end = idx;
                while (start > 0 && char.IsLetter(rtb.Text[start - 1])) start--;
                while (end < rtb.TextLength - 1 && char.IsLetter(rtb.Text[end + 1])) end++;
                return rtb.Text.Substring(start, end - start + 1);
            }
            catch { return null; }
        }

        /// <summary>用默认浏览器打开有道词典查词页。</summary>
        private static void OpenYoudaoDict(string word)
        {
            if (string.IsNullOrEmpty(word)) return;
            try
            {
                string url = "https://dict.youdao.com/result?word="
                    + Uri.EscapeDataString(word) + "&lang=en";
                System.Diagnostics.Process.Start(url);
            }
            catch { /* 默认浏览器不可用时静默失败，不影响阅读 */ }
        }

        /// <summary>
        /// 学后微阅读弹窗：本轮学过单词的例句串读，目标词高亮显示。
        /// items[i] = {headWord, sentence, sentenceCN}。
        /// callback(0) = 点击"开始测验"按钮；callback(-1) = 超时。
        /// </summary>
        public static void ShowReadingPopup(List<string[]> items,
            Action<int> callback, int timeoutMs = TIMEOUT_READING)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(-1); return; }

            dispatcher.BeginInvoke(new Action(() =>
            {
                _current?.Close();
                _current?.Dispose();

                int formW = 560, pad = 20;
                int cw = formW - pad * 2;

                var fontTitle = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
                var fontSent  = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
                var fontSentB = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
                var fontCN    = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

                string title = "学后微阅读 — 在句子里认出刚学的词";
                int hTitle = MeasureH(title, fontTitle, cw);

                // 预估内容高度（RichTextBox 超出部分自动滚动，估算偏差无害）
                int estH = 0;
                foreach (var it in items)
                {
                    estH += MeasureH("① " + it[1], fontSent, cw - 24) + 2;
                    if (!string.IsNullOrEmpty(it[2]))
                        estH += MeasureH(it[2], fontCN, cw - 24);
                    estH += 12;   // 条目间距
                }

                int btnH = 44;
                // 窗口高度 = 内容自适应，上界 = 屏幕 75%（仅达到上界才允许正文区滚动条）
                int formH = PAD_TOP + hTitle + LAYOUT_GAP + estH + LAYOUT_GAP + btnH + PAD_BOTTOM;
                formH = Math.Max(MIN_FORM_H, Math.Min(formH, CapH()));

                var form = new Form
                {
                    Text = "ToastFish",
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    Width = formW,
                    Height = formH,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.White
                };
                var wa = Screen.PrimaryScreen.WorkingArea;
                form.Left = wa.Right - form.Width - 20;
                form.Top = wa.Bottom - form.Height - 20;

                int y = 14;
                var tlbl = NewFixedLabel(title, Color.FromArgb(255, 200, 60), fontTitle, pad, y, cw, hTitle);
                form.Controls.Add(tlbl);
                y += hTitle + 10;

                // === 正文：RichTextBox 实现句内目标词高亮，超长自动滚动 ===
                var rtb = new RichTextBox
                {
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.FromArgb(200, 200, 200),
                    Location = new Point(pad, y),
                    Size = new Size(cw, formH - y - btnH - 28),
                    ScrollBars = RichTextBoxScrollBars.None,   // 初始无滚动条，Shown 校正时才按需开启
                    TabStop = false,
                    DetectUrls = false,
                    Font = fontSent
                };
                rtb.RightMargin = cw - 28;   // 文字与右缘留白，不贴滚动条
                int contentH = 0;            // ContentsResized 报告的真实渲染高度
                rtb.ContentsResized += (s, e) => contentH = e.NewRectangle.Height;
                string[] nums = { "①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧" };
                for (int i = 0; i < items.Count; i++)
                {
                    string hw = items[i][0], sent = items[i][1], cn = items[i][2];
                    string prefix = (i < nums.Length ? nums[i] : "·") + " ";

                    int hs, hl;
                    bool hit = Model.PushControl.WordFormMatcher.TryFindInSentence(hw, sent, out hs, out hl);

                    rtb.SelectionFont = fontSent;
                    rtb.SelectionColor = Color.FromArgb(200, 200, 200);
                    rtb.AppendText(prefix);
                    if (hit)
                    {
                        rtb.AppendText(sent.Substring(0, hs));
                        rtb.SelectionFont = fontSentB;
                        rtb.SelectionColor = Color.FromArgb(255, 200, 60);
                        rtb.AppendText(sent.Substring(hs, hl));
                        rtb.SelectionFont = fontSent;
                        rtb.SelectionColor = Color.FromArgb(200, 200, 200);
                        rtb.AppendText(sent.Substring(hs + hl));
                    }
                    else
                    {
                        rtb.AppendText(sent);   // 匹配不到则整句正常显示（优雅降级）
                    }
                    rtb.AppendText("\n");
                    if (!string.IsNullOrEmpty(cn))
                    {
                        rtb.SelectionFont = fontCN;
                        rtb.SelectionColor = Color.FromArgb(130, 130, 130);
                        rtb.AppendText(cn + "\n");
                    }
                    if (i < items.Count - 1) rtb.AppendText("\n");
                }
                rtb.SelectionStart = 0;
                rtb.SelectionLength = 0;
                form.Controls.Add(rtb);
                AttachWordLookup(rtb);   // 点击任意单词查有道词典（悬停变手型）

                // 高度校正已移至下方 btn 创建后的 form.Shown（ArrangeReading）中执行

                // === 按钮（宽度按文字实测，避免截断） ===
                string btnText = "读完了，开始测验 (ALT+1)";
                int btnW = TextRenderer.MeasureText(btnText, FONT_BTN).Width + 36;
                var btn = new Button
                {
                    Text = btnText,
                    Size = new Size(btnW, btnH),
                    BackColor = Color.FromArgb(51, 51, 51),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = FONT_BTN
                };
                btn.Location = new Point((form.ClientSize.Width - btn.Width) / 2,
                                         form.ClientSize.Height - btnH - 14);
                btn.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
                btn.Click += (s, e) => { form.Close(); callback(0); };
                form.Controls.Add(btn);

                // === 按真实内容高度校正窗口 ===
                // 原实现：Show() 之前同步读 contentH → 句柄未建、ContentsResized 未触发 → 恒为 0，
                // 窗口被钳到最小高度、短内容也出现滚动条。改为在 Shown 事件（句柄已建、事件已触发）里校正。
                // 滚动条唯一出现条件 = 内容高度超过 75% 屏高上界。
                bool arranged = false;
                void ArrangeReading()
                {
                    int fixedTop = y;   // 标题已排到 y（rtb.Top）
                    int overhead = LAYOUT_GAP + btnH + PAD_BOTTOM;
                    int content = contentH > 0 ? contentH : RealRtbHeight(rtb);
                    int desired = fixedTop + content + overhead;
                    bool scroll = desired > CapH();
                    int newH = Math.Max(MIN_FORM_H, Math.Min(desired, CapH()));
                    if (newH != form.Height)
                    {
                        form.SuspendLayout();
                        form.Height = newH;
                        form.Top = wa.Bottom - form.Height - 20;
                        form.ResumeLayout();
                    }
                    rtb.ScrollBars = scroll ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None;
                    rtb.Height = Math.Max(0, form.ClientSize.Height - fixedTop - overhead);
                    // 按钮锚定窗体底部（原定位依赖 Show 前旧 ClientSize，必须在重排后重算）
                    btn.Location = new Point((form.ClientSize.Width - btn.Width) / 2,
                                             form.ClientSize.Height - btnH - 14);
                }
                form.Shown += (s, e) => { if (!arranged) { arranged = true; ArrangeReading(); } };

                System.Windows.Forms.Timer timer = null;
                if (timeoutMs > 0)   // 0 = 无时限，阅读材料不限时
                {
                    timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                    timer.Tick += (s, e) => { timer.Stop(); callback(-1); form.Close(); };
                    timer.Start();
                    form.Tag = new PopupState { Timer = timer, TotalMs = timeoutMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };
                }

                form.FormClosed += (s, e) =>
                {
                    timer?.Dispose();
                    if (_current == form) _current = null;
                    _isHidden = false;
                    form.Dispose();
                };

                _isHidden = false;
                _current = form;
                form.Show();
                form.BringToFront();   // 重新抢回置顶带顶部（其他 TopMost 窗口可能在上）
                btn.Focus();   // 避免 RichTextBox 获得焦点显示光标
                // 排队一次置顶重申（原理见 ShowQuizPopup 处注释）
                form.BeginInvoke(new Action(() => { form.TopMost = true; form.BringToFront(); }));
            }));
        }

        /// <summary>
        /// 学后 AI 短文弹窗：AI 生成的连贯短文 + 中文翻译，文中所有学过的词高亮。
        /// essayWords 非 null 时先展示阅读前信息页（2026-07-21）。
        /// </summary>
        public static void ShowEssayPopup(string essayEN, string essayCN,
            List<string> highlightWords, List<ToastFish.Model.SqliteControl.Word> essayWords,
            List<Model.Ai.EssayQuestion> questions,
            Action<int> callback, int timeoutMs = TIMEOUT_READING)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(-1); return; }

            dispatcher.BeginInvoke(new Action(() =>
            {
                // === Phase 1：阅读前信息页（2026-07-21） ===
                if (essayWords != null && essayWords.Count > 0)
                {
                    ShowEssayInfoPage(essayEN, essayCN, highlightWords, essayWords, questions, callback, timeoutMs);
                    return;
                }

                // 无 Word 列表（回退）：直接进入 Phase 2（无测验）
                ShowEssayContent(essayEN, essayCN, highlightWords, null, callback, timeoutMs);
            }));
        }

        /// <summary>Phase 1：阅读前信息页，展示词数统计和预计时间。</summary>
        private static void ShowEssayInfoPage(string essayEN, string essayCN,
            List<string> highlightWords, List<ToastFish.Model.SqliteControl.Word> essayWords,
            List<Model.Ai.EssayQuestion> questions,
            Action<int> callback, int timeoutMs)
        {
            // 关闭上一个弹窗（测验弹窗等）
            _current?.Close();
            _current?.Dispose();

            int formW = 480, pad = 24;
            int cw = formW - pad * 2;

            var fontTitle = new Font("Microsoft YaHei UI", 13, FontStyle.Bold);
            var fontBody = new Font("Microsoft YaHei UI", 11, FontStyle.Regular);
            var fontSmall = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

            // 词数统计 + 短文信息
            int easy = 0, normal = 0, hard = 0;
            foreach (var w in essayWords)
            {
                double d = Math.Max(0.0, Math.Min(1.0, w.difficulty));
                if (d < 0.1) easy++;
                else if (d < 0.3) normal++;
                else hard++;
            }
            // 计算短文词数（简单按空格分词）
            int essayWordCount = essayEN.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
            Model.Ai.AiConfig.Load();
            string estTime = Model.Ai.AiConfig.ModelMode == 1 ? "约 3-5 分钟" : "约 2 分钟";

            string title = "📖 学后 AI 短文";
            int hTitle = MeasureH(title, fontTitle, cw);
            string infoLine = "短文约 " + essayWordCount + " 词  ·  包含你刚学的 " + essayWords.Count + " 个目标词";
            int hInfo = MeasureH(infoLine, fontBody, cw);
            string statLine = "简单词 " + easy + " 个  ·  普通词 " + normal + " 个  ·  困难词 " + hard + " 个";
            int hStat = MeasureH(statLine, fontSmall, cw);
            string timeLine = "预计阅读时间：" + estTime;
            int hTime = MeasureH(timeLine, fontSmall, cw);

            int btnH = 44;
            // 高度 = 内容自适应，上界统一 75%（2026-08-08 布局机制重设计）
            int formH = PAD_TOP + hTitle + 14 + hInfo + 8 + hStat + 8 + hTime + 14 + btnH + PAD_BOTTOM;
            formH = Math.Max(MIN_FORM_H, Math.Min(formH, CapH()));

            var form = new Form
            {
                Text = "ToastFish",
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                Width = formW,
                Height = formH,
                BackColor = Color.FromArgb(31, 31, 31),
                ForeColor = Color.White
            };
            var wa = Screen.PrimaryScreen.WorkingArea;
            form.Left = wa.Right - form.Width - 20;
            form.Top = wa.Bottom - form.Height - 20;

            int y = PAD_TOP;
            var lblTitle = NewFixedLabel(title, Color.FromArgb(255, 200, 60), fontTitle, pad, y, cw, hTitle);
            form.Controls.Add(lblTitle);
            y += hTitle + 14;

            var lblInfo = NewFixedLabel(infoLine, Color.FromArgb(220, 220, 220), fontBody, pad, y, cw, hInfo);
            form.Controls.Add(lblInfo);
            y += hInfo + 8;

            var lblStat = NewFixedLabel(statLine, Color.FromArgb(160, 160, 180), fontSmall, pad, y, cw, hStat);
            form.Controls.Add(lblStat);
            y += hStat + 8;

            var lblTime = NewFixedLabel(timeLine, Color.FromArgb(160, 160, 180), fontSmall, pad, y, cw, hTime);
            form.Controls.Add(lblTime);

            string btnText = "开始阅读 (ALT+1)";
            int btnW = TextRenderer.MeasureText(btnText, FONT_BTN).Width + 36;
            var btn = new Button
            {
                Text = btnText,
                Size = new Size(btnW, btnH),
                BackColor = Color.FromArgb(51, 51, 51),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = FONT_BTN
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
            int bx = (form.ClientSize.Width - btnW) / 2;
            int by2 = form.ClientSize.Height - btnH - 14;
            btn.Location = new Point(bx, by2);

            bool phase2Entered = false;
            Action enterPhase2 = () =>
            {
                if (phase2Entered) return;
                phase2Entered = true;
                form.Close();
                ShowEssayContent(essayEN, essayCN, highlightWords, questions, callback, timeoutMs);
            };
            btn.Click += (s, e) => enterPhase2();
            form.Controls.Add(btn);

            // 监听全局 ALT+1 热键（复用 HotKeytObservable）
            IDisposable hotkeySub = null;
            try
            {
                hotkeySub = Model.PushControl.PushWords.HotKeytObservable.Subscribe(
                    key => { if (key == "1") enterPhase2(); });
            }
            catch { }

            System.Windows.Forms.Timer phase1Timer = null;
            if (timeoutMs > 0)
            {
                phase1Timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                phase1Timer.Tick += (s, e) => { phase1Timer.Stop(); form.Close(); callback(-1); };
                phase1Timer.Start();
            }

            form.FormClosed += (s, e) =>
            {
                phase1Timer?.Dispose();
                hotkeySub?.Dispose();
                if (_current == form) _current = null;
                _isHidden = false;
                form.Dispose();
            };

            _isHidden = false;
            _current = form;
            form.Show();
            form.BringToFront();
            btn.Focus();
            form.BeginInvoke(new Action(() => { form.TopMost = true; form.BringToFront(); }));
        }

        /// <summary>Phase 2：短文阅读 + 内嵌测验（2026-07-21 重构）。</summary>
        private static void ShowEssayContent(string essayEN, string essayCN,
            List<string> highlightWords, List<Model.Ai.EssayQuestion> questions,
            Action<int> callback, int timeoutMs)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(-1); return; }

            dispatcher.BeginInvoke(new Action(() =>
            {
                _current?.Close();
                _current?.Dispose();

                int formW = 560, pad = 20;
                int cw = formW - pad * 2;

                var fontTitle = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
                var fontSent  = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
                var fontSentB = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
                var fontQuiz  = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);
                var fontQuizB = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
                var fontSmall = new Font("Microsoft YaHei UI", 9, FontStyle.Regular);

                string title = "学后 AI 短文 — 在文章里认出刚学的词";
                int hTitle = MeasureH(title, fontTitle, cw);
                int hPassageMeasured = MeasureH(essayEN, fontSent, cw - 28);
                int panelH_reading = 70;   // 阅读阶段底部按钮面板（开始测验 + 显示翻译）
                int maxPassageH = CapH() - (PAD_TOP + hTitle + LAYOUT_GAP + 8 + panelH_reading + 10); // 短文区可用高 = 75% 屏高减其余开销
                int passageH = Math.Min(hPassageMeasured + 180, maxPassageH); // +180 兜底高亮词加宽的换行增幅（占位，Shown 校正）
                int formH = PAD_TOP + hTitle + LAYOUT_GAP + passageH + 8 + panelH_reading + 10;
                formH = Math.Max(MIN_FORM_H, Math.Min(formH, CapH()));

                var rtb = new RichTextBox
                {
                    ReadOnly = true, BorderStyle = BorderStyle.None,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.FromArgb(200, 200, 200),
                    Size = new Size(cw, passageH),
                    ScrollBars = RichTextBoxScrollBars.None,  // 占位；Shown 校正时按真实内容高度决定是否开启
                    TabStop = false, DetectUrls = false, Font = fontSent
                };
                rtb.RightMargin = cw - 28;
                int essayContentH = 0;   // ContentsResized 报告的真实渲染高度（Shown 时已就绪，比 RealRtbHeight 更可靠）
                rtb.ContentsResized += (s, e) => essayContentH = e.NewRectangle.Height;

                var form = new Form
                {
                    Text = "ToastFish", FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
                    TopMost = true, Width = formW, Height = formH,
                    BackColor = Color.FromArgb(31, 31, 31), ForeColor = Color.White
                };
                var wa = Screen.PrimaryScreen.WorkingArea;
                form.Left = wa.Right - form.Width - 20;
                form.Top = wa.Bottom - form.Height - 20;

                int y = PAD_TOP;
                form.Controls.Add(NewFixedLabel(title, Color.FromArgb(255, 200, 60), fontTitle, pad, y, cw, hTitle));
                y += hTitle + LAYOUT_GAP;

                rtb.Location = new Point(pad, y);
                form.Controls.Add(rtb);
                y += passageH + 8;

                var bottomPanel = new Panel
                {
                    Location = new Point(pad, y),
                    Size = new Size(cw, panelH_reading),
                    BackColor = Color.FromArgb(31, 31, 31)
                };
                form.Controls.Add(bottomPanel);

                // === 逐 token 渲染短文 ===
                var tokenRe = new System.Text.RegularExpressions.Regex(
                    @"[^\W\d_][^\W\d_]*(?:['\-][^\W\d_]+)*");
                int pos = 0;
                foreach (System.Text.RegularExpressions.Match m in tokenRe.Matches(essayEN))
                {
                    bool hit = false;
                    foreach (string hw in highlightWords)
                        if (Model.PushControl.WordFormMatcher.WordMatch(hw, m.Value)) { hit = true; break; }
                    if (m.Index > pos)
                    {
                        rtb.SelectionFont = fontSent;
                        rtb.SelectionColor = Color.FromArgb(200, 200, 200);
                        rtb.AppendText(essayEN.Substring(pos, m.Index - pos));
                    }
                    rtb.SelectionFont = hit ? fontSentB : fontSent;
                    rtb.SelectionColor = hit ? Color.FromArgb(255, 200, 60) : Color.FromArgb(200, 200, 200);
                    rtb.AppendText(m.Value);
                    pos = m.Index + m.Length;
                }
                if (pos < essayEN.Length)
                {
                    rtb.SelectionFont = fontSent;
                    rtb.SelectionColor = Color.FromArgb(200, 200, 200);
                    rtb.AppendText(essayEN.Substring(pos));
                }
                rtb.SelectionStart = 0; rtb.SelectionLength = 0;
                AttachWordLookup(rtb);   // 点击任意单词查有道词典（悬停变手型）

                // === 测验状态 ===
                int quizIdx = -1, correctCnt = 0, attemptedCnt = 0;

                // ---- 阅读阶段按钮 ----
                int btnH = 40;
                string readBtnText = "读完了，开始测验 (ALT+1)";
                int readBtnW = TextRenderer.MeasureText(readBtnText, FONT_BTN).Width + 36;
                var btnQuiz = new Button
                {
                    Text = readBtnText, Size = new Size(readBtnW, btnH),
                    BackColor = Color.FromArgb(51, 51, 51), ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat, Font = FONT_BTN
                };
                btnQuiz.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
                bottomPanel.Controls.Add(btnQuiz);

                // 布局阅读阶段按钮（「显示翻译」已移除，翻译见仪表盘 AI 短文历史页）
                Action layoutReadingBtns = () =>
                {
                    int tw = btnQuiz.Width;
                    int x = (bottomPanel.ClientSize.Width - tw) / 2;
                    int by = (bottomPanel.ClientSize.Height - btnH) / 2;
                    btnQuiz.Location = new Point(x, by);
                };
                layoutReadingBtns();

                // === 阅读阶段布局校正（Shown 校正 + 「显示翻译」后共用） ===
                // 规则：窗口高度 = 短文真实渲染高度 + 固定开销，上界 75%；仅超过上界才开启正文区滚动条
                bool readingArranged = false;
                Action ArrangeReadingStage = () =>
                {
                    int fixedTop = rtb.Top;   // rtb 顶部（注意：不能用 y——y 在 rtb 定位后已累加到 rtb 底部）
                    int overhead = 8 + panelH_reading + 10;
                    int content = essayContentH > 0 ? essayContentH : (rtb.TextLength > 0 ? RealRtbHeight(rtb) : 0);
                    int desired = fixedTop + content + overhead;
                    bool scroll = desired > CapH();
                    int newH = Math.Max(MIN_FORM_H, Math.Min(desired, CapH()));
                    if (newH != form.Height)
                    {
                        form.SuspendLayout();
                        form.Height = newH;
                        form.Top = wa.Bottom - form.Height - 20;
                        form.ResumeLayout();
                    }
                    rtb.ScrollBars = scroll ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None;
                    rtb.Height = Math.Max(0, form.ClientSize.Height - fixedTop - overhead);
                    bottomPanel.Top = rtb.Bottom + 8;
                    layoutReadingBtns();
                };

                // ====== 测验内嵌逻辑 ======
                bool hasQuiz = (questions != null && questions.Count > 0);

                // 修正按钮文案（创建时用了默认文案）
                btnQuiz.Text = hasQuiz ? "读完了，开始测验 (ALT+1)" : "读完了 (ALT+1)";
                btnQuiz.Size = new Size(
                    TextRenderer.MeasureText(btnQuiz.Text, FONT_BTN).Width + 36, btnH);
                layoutReadingBtns();

                Action startQuiz = () =>
                {
                    if (quizIdx >= 0) return;
                    if (!hasQuiz) { form.Close(); callback(0); return; }
                    quizIdx = 0;

                    // RTB 拉伸到测验面板上方；面板高度由 ShowQuizInPanel 返回，窗体钳制到 75% 上界
                    int panelTop = rtb.Top + Math.Max(200, passageH - 60); // 至少留 200px 给短文
                    int panelH = 220; // 占位，首次 showNext 由 ShowQuizInPanel 返回校正
                    panelTop = Math.Min(panelTop, CapH() - panelH - 10);   // 钳制：panelTop+panelH 不越 75% 屏高
                    bottomPanel.Location = new Point(pad, panelTop);
                    bottomPanel.Height = panelH;
                    rtb.Height = Math.Max(0, panelTop - rtb.Top - 6);
                    rtb.ScrollBars = RichTextBoxScrollBars.Vertical;

                    int newFH = Math.Min(bottomPanel.Bottom + 10, CapH());
                    if (newFH != form.Height)
                    {
                        form.Height = newFH;
                        form.Top = wa.Bottom - newFH - 20;
                    }

                    int cc = 0, ac = 0;
                    Action onCorrect = () => { cc++; ac++; };
                    Action onWrong = () => { ac++; };

                    Action showNext = null;
                    int qi = 0;
                    showNext = () =>
                    {
                        if (qi < questions.Count)
                        {
                            int qContentH = ShowQuizInPanel(bottomPanel, questions, qi, fontQuiz, fontQuizB, fontSmall,
                                onCorrect, onWrong, () => { qi++; showNext(); });
                            // 面板高度 = 该题内容高（题目+结果条+选项），超 75% 上界内可用高时启用面板滚动
                            int available = CapH() - (bottomPanel.Top + 10);
                            int panelH_new = Math.Max(120, Math.Min(qContentH, available));
                            bottomPanel.Height = panelH_new;
                            bottomPanel.AutoScroll = qContentH > panelH_new;
                            bottomPanel.AutoScrollMinSize = qContentH > panelH_new
                                ? new Size(bottomPanel.ClientSize.Width, qContentH) : Size.Empty;
                            bottomPanel.AutoScrollPosition = new Point(0, 0); // 切题滚回顶部
                            int fh = Math.Min(bottomPanel.Bottom + 10, CapH());
                            if (fh != form.Height) { form.Height = fh; form.Top = wa.Bottom - fh - 20; }
                        }
                        else
                        {
                            ShowQuizSummary(bottomPanel, cc, ac, fontQuiz);
                            // 「关闭」按钮（双保险：点击或 5 秒后自动关闭）
                            var closeBtn = new Button
                            {
                                Text = "关闭 (5s 后自动关闭)",
                                Size = new Size(bottomPanel.ClientSize.Width - 16, 36),
                                Location = new Point(8, bottomPanel.ClientSize.Height - 44),
                                BackColor = Color.FromArgb(0, 100, 70),
                                ForeColor = Color.FromArgb(200, 255, 220),
                                FlatStyle = FlatStyle.Flat,
                                Font = FONT_BTN
                            };
                            closeBtn.FlatAppearance.BorderColor = Color.FromArgb(0, 150, 100);
                            bool closed = false;
                            Action doClose = () =>
                            {
                                if (closed) return; closed = true;
                                form.Close(); callback((ac << 16) | cc);
                            };
                            closeBtn.Click += (s2, e2) => doClose();
                            bottomPanel.Controls.Add(closeBtn);
                            // 备用定时器（WinForms Timer 偶发不触发，按钮兜底）
                            var closeTimer = new System.Windows.Forms.Timer { Interval = 5000 };
                            closeTimer.Tick += (s2, e2) => { closeTimer.Stop(); doClose(); };
                            closeTimer.Start();
                        }
                    };
                    showNext();
                };

                btnQuiz.Click += (s, e) => startQuiz();

                // ALT+1 热键开始测验（Phase 2 独立注册，Phase 1 的热键已随 form 关闭释放）
                IDisposable quizHotkey = null;
                try { quizHotkey = Model.PushControl.PushWords.HotKeytObservable.Subscribe(
                    key => { if (key == "1") startQuiz(); }); } catch { }

                // 超时管理
                System.Windows.Forms.Timer timer = null;
                if (timeoutMs > 0)
                {
                    timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                    timer.Tick += (s, e) => { timer.Stop(); callback((attemptedCnt << 16) | correctCnt); form.Close(); };
                    timer.Start();
                    form.Tag = new PopupState { Timer = timer, TotalMs = timeoutMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };
                }

                form.FormClosed += (s, e) =>
                {
                    quizHotkey?.Dispose();
                    timer?.Dispose();
                    if (_current == form) _current = null;
                    _isHidden = false;
                    form.Dispose();
                };

                // Shown 时句柄已建、内容已渲染，按真实高度做一次最终布局校正
                form.Shown += (s, e) => { if (!readingArranged) { readingArranged = true; ArrangeReadingStage(); } };

                _isHidden = false;
                _current = form;
                form.Show();
                form.BringToFront();
                btnQuiz.Focus();
                form.BeginInvoke(new Action(() => { form.TopMost = true; form.BringToFront(); }));
            }));
        }

        /// <summary>在内嵌面板中展示一道测验题（2026-07-21）。</summary>
        /// <summary>在内嵌面板中展示一道测验题。返回该题所需内容高度（2026-08-08 改为返回 int，供面板自适应高度）。</summary>
        private static int ShowQuizInPanel(Panel panel,
            List<Model.Ai.EssayQuestion> questions, int idx,
            Font fontQ, Font fontQB, Font fontS,
            Action onCorrect, Action onWrong, Action onNext)
        {
            panel.Controls.Clear();
            var q = questions[idx];
            int pad = 8;
            int cw = panel.ClientSize.Width - pad * 2;

            // 题目标签
            string qText = "Q" + (idx + 1) + ". " + q.Question;
            int qH = MeasureH(qText, fontQB, cw);
            var lblQ = new Label
            {
                Text = qText, AutoSize = false, Size = new Size(cw, qH),
                Location = new Point(pad, 8),
                ForeColor = Color.FromArgb(220, 220, 255),
                Font = fontQB, BackColor = Color.Transparent
            };
            panel.Controls.Add(lblQ);

            // 答案状态标签：固定尺寸结果条（按最长错误消息量定高度），位于题目下方、选项上方。
            // 旧版与第一个选项按钮同 y 且按钮后 Add 在上层 → 结果文字被按钮盖住；此布局从根上消除重叠。
            string resultMsg = "✗ 正确答案是 X — 在上面短文里找找对应的部分";
            int resultStripH = MeasureH(resultMsg, fontS, cw);
            var lblResult = new Label
            {
                Text = "", AutoSize = false,
                Size = new Size(cw, resultStripH),
                Location = new Point(pad, 8 + qH + 6),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = fontS, BackColor = Color.Transparent, Visible = false
            };
            panel.Controls.Add(lblResult);
            lblResult.BringToFront();

            // 选项按钮（从结果条下方开始纵向堆叠）
            int startBtnY = 8 + qH + 6 + resultStripH + 4;
            int btnY = startBtnY;
            int btnW = cw - 16;
            List<Button> choiceBtns = new List<Button>();
            bool answered = false;

            for (int i = 0; i < q.Choices.Count; i++)
            {
                string label = (char)('A' + i) + ". " + q.Choices[i];
                int labelH = TextRenderer.MeasureText(label, fontS,
                    new Size(btnW - 16, 9999),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                int bH = Math.Max(34, labelH + 10);
                var b = new Button
                {
                    Text = label, Size = new Size(btnW, bH),
                    Location = new Point(pad + 8, btnY),
                    BackColor = Color.FromArgb(44, 44, 60),
                    ForeColor = Color.FromArgb(200, 200, 200),
                    FlatStyle = FlatStyle.Flat, Font = fontS,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 90);
                int cIdx = i;
                b.Click += (s, e) =>
                {
                    if (answered) return;
                    answered = true;
                    // attempted counted by onAttempt callback
                    bool correct2 = (cIdx == q.Answer);

                    // 高亮：绿=正确，红=选错
                    foreach (var cb in choiceBtns)
                    {
                        int bi = choiceBtns.IndexOf(cb);
                        if (bi == q.Answer)
                        {
                            cb.Enabled = false;
                            cb.BackColor = Color.FromArgb(0, 100, 50);
                            cb.ForeColor = Color.FromArgb(0, 255, 136);
                        }
                        else if (bi == cIdx && !correct2)
                        {
                            cb.Enabled = false;
                            cb.BackColor = Color.FromArgb(100, 20, 40);
                            cb.ForeColor = Color.FromArgb(255, 51, 85);
                        }
                        else
                        {
                            cb.Enabled = false;
                        }
                    }

                    // 结果反馈
                    lblResult.Visible = true;
                    if (correct2)
                    {
                        onCorrect();
                        lblResult.Text = "✓ 正确！";
                        lblResult.ForeColor = Color.FromArgb(0, 255, 136);
                    }
                    else
                    {
                        onWrong();
                        lblResult.Text = "✗ 正确答案是 " + (char)('A' + q.Answer) + " — 在上面短文里找找对应的部分";
                        lblResult.ForeColor = Color.FromArgb(255, 130, 100);
                    }

                    // 延迟进入下一题
                    var delay = new System.Windows.Forms.Timer { Interval = correct2 ? 1500 : 3500 };
                    delay.Tick += (s2, e2) => { delay.Stop(); delay.Dispose(); onNext(); };
                    delay.Start();
                };
                panel.Controls.Add(b);
                choiceBtns.Add(b);
                btnY += bH + 4;
            }

            // 返回该题所需内容高（顶部8 + 题目 + 结果条 + 选项 + 底部8）
            return btnY + 8;
        }

        /// <summary>测验结束摘要（2026-07-21）。</summary>
        private static void ShowQuizSummary(Panel panel, int correct, int attempted, Font font)
        {
            panel.Controls.Clear();
            string msg;
            Color msgColor;
            if (attempted == 0)
            {
                msg = "你跳过了测验，短文阅读已完成";
                msgColor = Color.FromArgb(160, 160, 180);
            }
            else if (correct == attempted)
            {
                msg = "全部正确！" + attempted + "/" + attempted + "  🎉";
                msgColor = Color.FromArgb(0, 255, 136);
            }
            else
            {
                msg = "阅读完成 — " + correct + "/" + attempted + " 题正确，字词掌握度已自动调整";
                msgColor = Color.FromArgb(255, 200, 100);
            }
            int mH = MeasureH(msg, font, panel.ClientSize.Width - 16);
            var lbl = new Label
            {
                Text = msg, AutoSize = false,
                Size = new Size(panel.ClientSize.Width - 16, mH),
                Location = new Point(8, (panel.ClientSize.Height - mH) / 2),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = msgColor, Font = font,
                BackColor = Color.Transparent
            };
            panel.Controls.Add(lbl);
        }

        public static void ToggleVisibility()
        {
            if (_current == null || _current.IsDisposed) return;

            var state = _current.Tag as PopupState;
            if (_isHidden)
            {
                // 重新显示：恢复剩余时间
                if (state?.Timer != null && state.PausedRemainingMs > 0)
                {
                    state.Timer.Interval = state.PausedRemainingMs;
                    state.Timer.Start();
                    state.StartedAt = DateTime.Now;
                    state.PausedRemainingMs = -1;
                }
                _current.Show();
                _isHidden = false;
            }
            else
            {
                // 隐藏：停止 Timer，记录剩余时间
                if (state?.Timer != null)
                {
                    state.Timer.Stop();
                    int elapsed = (int)(DateTime.Now - state.StartedAt).TotalMilliseconds;
                    state.PausedRemainingMs = Math.Max(1000, state.TotalMs - elapsed);
                }
                _current.Hide();
                _isHidden = true;
            }
        }

        /// <summary>
        /// 关闭当前活跃弹窗（供外部 Stop 调用）
        /// </summary>
        public static void CloseCurrent()
        {
            if (_current != null && !_current.IsDisposed)
            {
                try { _current.Close(); } catch { }
            }
        }

        /// <summary>
        /// 惊喜复习弹窗 —— 3 选项 + 60 秒超时
        /// 与 ShowQuizPopup 结构相同但超时更短，样式略有区分
        /// </summary>
        public static void ShowSurpriseQuizPopup(string title, string question,
            string[] choices, Action<int> callback, int timeoutMs = TIMEOUT_SURPRISE)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(-1); return; }

            dispatcher.BeginInvoke(new Action(() =>
            {
                _current?.Close();
                _current?.Dispose();

                int formW = 500, padX = 20, padTop = 14;
                int cw = formW - padX * 2;

                // 预计算标题 + 题目高度
                int hTitle = string.IsNullOrEmpty(title) ? 0 :
                    TextRenderer.MeasureText(title, FONT_BTN,
                        new Size(cw, 9999),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                int hQuestion = string.IsNullOrEmpty(question) ? 0 :
                    TextRenderer.MeasureText(question, FONT_WORD,
                        new Size(cw, 9999),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;

                // 按钮区域：高度按文字实测自适应（长释义自动换行完整显示），最小 42px
                int btnGap = 8;
                int btnW = cw;
                int[] btnHs = new int[choices.Length];
                int btnAreaH = (choices.Length - 1) * btnGap;
                for (int i = 0; i < choices.Length; i++)
                {
                    int th = string.IsNullOrEmpty(choices[i]) ? 0 :
                        TextRenderer.MeasureText(choices[i], FONT_BTN,
                            new Size(btnW - 24, 9999),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    btnHs[i] = Math.Max(42, th + 14);
                    btnAreaH += btnHs[i];
                }

                int contentH = padTop
                    + (hTitle > 0 ? hTitle + 6 : 0)
                    + (hQuestion > 0 ? hQuestion + 14 : 0)
                    + btnAreaH
                    + 20;

                int formH = contentH;
                bool needsScroll = contentH > (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.85);
                formH = Math.Min(formH, (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.85));
                formH = Math.Max(formH, 180);

                var form = new Form
                {
                    Text = "ToastFish",
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    Width = formW,
                    Height = formH,
                    BackColor = Color.FromArgb(31, 31, 31),
                    ForeColor = Color.White
                };

                // 内容超出屏高时启用滚动，选项不会被截断
                if (needsScroll)
                {
                    form.AutoScroll = true;
                    form.AutoScrollMinSize = new Size(formW - SystemInformation.VerticalScrollBarWidth, contentH);
                }

                var wa = Screen.PrimaryScreen.WorkingArea;
                form.Left = wa.Right - form.Width - 20;
                form.Top = wa.Bottom - form.Height - 20;

                int y = padTop;

                // 标题（惊喜复习用偏暖色调区分）
                if (!string.IsNullOrEmpty(title))
                {
                    var tlbl = NewFixedLabel(title,
                        Color.FromArgb(255, 200, 100), FONT_BTN, padX, y, cw, hTitle);
                    form.Controls.Add(tlbl);
                    y += hTitle + 6;
                }

                // 题目正文（稍大字体突出显示）
                if (!string.IsNullOrEmpty(question))
                {
                    var qlbl = NewFixedLabel(question,
                        Color.White, FONT_WORD, padX, y, cw, hQuestion);
                    form.Controls.Add(qlbl);
                    y += hQuestion + 14;
                }

                // 选项按钮（垂直堆叠，高度自适应）
                for (int i = 0; i < choices.Length; i++)
                {
                    int idx = i;
                    var btn = new Button
                    {
                        Text = choices[i],
                        Location = new Point(padX, y),
                        Size = new Size(btnW, btnHs[i]),
                        BackColor = Color.FromArgb(51, 51, 51),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Font = FONT_BTN,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Padding = new Padding(12, 0, 0, 0),
                        Tag = idx
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
                    btn.MouseEnter += (s, e) =>
                    {
                        btn.BackColor = Color.FromArgb(80, 60, 30);
                    };
                    btn.MouseLeave += (s, e) =>
                    {
                        btn.BackColor = Color.FromArgb(51, 51, 51);
                    };
                    btn.Click += (s, e) => { form.Close(); callback(idx); };
                    form.Controls.Add(btn);
                    y += btnHs[i] + btnGap;
                }

                var timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                timer.Tick += (s, e) => { timer.Stop(); callback(-1); form.Close(); };
                timer.Start();
                form.Tag = new PopupState { Timer = timer, TotalMs = timeoutMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };

                form.FormClosed += (s, e) =>
                {
                    timer.Dispose();
                    if (_current == form) _current = null;
                    _isHidden = false;
                    form.Dispose();
                };

                _isHidden = false;
                _current = form;
                form.Show();
            }));
        }

        /// <summary>
        /// 15选10 完形填空弹窗（2026-07-22 新增）。
        /// 短文含 10 个空，15 个候选词（10正确+5干扰），点词填入空位。
        /// callback(attempted << 16 | correct) —— 与 ShowEssayPopup 的测验回调签名一致。
        /// </summary>
        public static void ShowClozePopup(ClozeResult cloze, List<string> highlightWords,
            Action<int> callback, int timeoutMs = TIMEOUT_READING)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(0); return; }

            dispatcher.BeginInvoke(new Action(() =>
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                int pad = 10;
                int cw = Math.Min(680, wa.Width - 40);
                int formW = cw;
                int passageW = cw - 2 * pad;

                // 字体
                var fontPassage = new Font("Microsoft YaHei UI", 12, FontStyle.Regular);
                var fontPassageB = new Font("Microsoft YaHei UI", 12, FontStyle.Bold);
                var fontWord = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);
                var fontSmall = new Font("Microsoft YaHei UI", 9, FontStyle.Regular);
                var fontTitle = new Font("Microsoft YaHei UI", 13, FontStyle.Bold);
                var fontCN = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);

                // 计算正确词数量（决定提交门槛）
                int correctWordCount = 0;
                for (int b = 0; b < 10; b++)
                    if (cloze.Candidates.Exists(c => c.Blank == b)) correctWordCount++;
                int minToSubmit = Math.Max(correctWordCount - 1, correctWordCount >= 6 ? 6 : correctWordCount);

                // === 提前声明闭包变量 ===
                RichTextBox rtb = null;
                Button submitBtn = null;
                Button translateBtn = null;
                Label subtitleLbl = null;
                RichTextBox translateLbl = null;   // 翻译区（RTB 支持内容超高时自身滚动）
                Label blankLabel = null; Label candLabel = null;
                FlowLayoutPanel blankPanel = null; FlowLayoutPanel candPanel = null;
                int blankHintH = 0; int candHintH = 0;
                System.Windows.Forms.Timer clTimer = null;
                Button[] blankBtns = new Button[10];
                List<Button> candBtns = new List<Button>();
                string[] filledWords = new string[10];
                int[] filledCandIdx = new int[10];
                string[] correctWords = new string[10];
                var usedCandidates = new HashSet<int>();
                int filledCount = 0;
                int selBlank = -1;
                bool submitted = false;
                for (int b = 0; b < 10; b++) filledCandIdx[b] = -1;

                for (int b = 0; b < 10; b++)
                {
                    var c = cloze.Candidates.Find(x => x.Blank == b);
                    correctWords[b] = c != null ? c.Word : null;
                }

                // 构造段落文本：把 [N] 替换为可视化空位
                string BuildPassage()
                {
                    string t = cloze.Text;
                    // 逆序替换避免 [10] 被 [1] 误匹配
                    for (int i = 10; i >= 1; i--)
                    {
                        string marker = "[" + i + "]";
                        string repl;
                        if (filledWords[i - 1] != null)
                            repl = " ▶" + filledWords[i - 1] + "◀ "; // ▶word◀
                        else if (selBlank == i - 1)
                            repl = " ┌_" + i + "_┐ "; // ┌_N_┐ highlighted
                        else
                            repl = " ___" + i + "___ ";
                        // 使用 IndexOf 精准替换，避免 Replace 的全局副作用
                        int idx = t.IndexOf(marker);
                        if (idx >= 0)
                            t = t.Substring(0, idx) + repl + t.Substring(idx + marker.Length);
                    }
                    return t;
                }

                // 渲染段落到 RichTextBox
                void RenderPassage()
                {
                    rtb.SuspendLayout();
                    string fullText = BuildPassage();
                    rtb.Text = fullText;
                    // 整体默认样式
                    rtb.SelectAll();
                    rtb.SelectionFont = fontPassage;
                    rtb.SelectionColor = Color.FromArgb(210, 210, 210);

                    // 逐空白标记上色
                    for (int i = 0; i < 10; i++)
                    {
                        string search;
                        Color clr;
                        if (filledWords[i] != null)
                        {
                            search = "▶" + filledWords[i] + "◀";
                            clr = Color.FromArgb(0, 255, 136); // 绿色
                        }
                        else if (selBlank == i)
                        {
                            search = "┌_" + (i + 1) + "_┐";
                            clr = Color.FromArgb(255, 200, 100); // 金色
                        }
                        else
                        {
                            search = "___" + (i + 1) + "___";
                            clr = Color.FromArgb(255, 180, 60); // 橙色
                        }
                        int pos = rtb.Text.IndexOf(search);
                        if (pos >= 0)
                        {
                            rtb.Select(pos, search.Length);
                            rtb.SelectionFont = fontPassageB;
                            rtb.SelectionColor = clr;
                        }
                    }
                    rtb.Select(0, 0);
                    rtb.ResumeLayout();
                }

                // 辅助：刷新空位按钮的外观
                void RefreshBlankBtns()
                {
                    for (int b = 0; b < 10; b++)
                    {
                        if (filledWords[b] != null)
                        {
                            blankBtns[b].Text = (b + 1) + ". " + filledWords[b];
                            blankBtns[b].BackColor = submitted
                                ? (filledWords[b] == correctWords[b] ? Color.FromArgb(0, 90, 50) : Color.FromArgb(90, 20, 40))
                                : Color.FromArgb(0, 70, 50);
                            blankBtns[b].ForeColor = submitted
                                ? (filledWords[b] == correctWords[b] ? Color.FromArgb(0, 255, 136) : Color.FromArgb(255, 80, 110))
                                : Color.FromArgb(0, 255, 136);
                        }
                        else
                        {
                            blankBtns[b].Text = (b + 1).ToString();
                            if (selBlank == b && !submitted)
                            {
                                blankBtns[b].BackColor = Color.FromArgb(80, 60, 20);
                                blankBtns[b].ForeColor = Color.FromArgb(255, 200, 100);
                            }
                            else
                            {
                                blankBtns[b].BackColor = Color.FromArgb(44, 44, 60);
                                blankBtns[b].ForeColor = Color.FromArgb(180, 180, 180);
                            }
                        }
                    }
                }

                void RefreshSubmitBtn()
                {
                    if (submitted)
                    {
                        int corr = 0;
                        for (int b = 0; b < 10; b++)
                            if (filledWords[b] != null && filledWords[b] == correctWords[b]) corr++;
                        submitBtn.Text = corr + "/" + correctWordCount + " 正确  —  点击关闭";
                        submitBtn.BackColor = Color.FromArgb(0, 100, 70);
                        submitBtn.ForeColor = Color.FromArgb(180, 255, 200);
                        submitBtn.Enabled = true;
                    }
                    else if (filledCount >= minToSubmit)
                    {
                        submitBtn.Text = "✓ 提交答案（已填 " + filledCount + " / 需≥" + minToSubmit + "）";
                        submitBtn.BackColor = Color.FromArgb(0, 120, 80);
                        submitBtn.ForeColor = Color.FromArgb(200, 255, 220);
                        submitBtn.Enabled = true;
                    }
                    else
                    {
                        submitBtn.Text = "提交答案（已填 " + filledCount + " / 需≥" + minToSubmit + "）";
                        submitBtn.BackColor = Color.FromArgb(60, 60, 60);
                        submitBtn.ForeColor = Color.FromArgb(140, 140, 140);
                        submitBtn.Enabled = false;
                    }
                }

                void UpdateSubtitle()
                {
                    subtitleLbl.Text = "已填: " + filledCount + "/" + correctWordCount;
                    subtitleLbl.ForeColor = filledCount >= minToSubmit
                        ? Color.FromArgb(0, 255, 136)
                        : Color.FromArgb(200, 180, 120);
                }

                void ClearSelection()
                {
                    selBlank = -1;
                }

                // === 构建 UI ===
                int titleH = 32;
                blankHintH = MeasureH("空位（点选后→再点候选词填入；再点已填空位可撤销）：", fontSmall, passageW) + 3;
                candHintH = MeasureH("候选词（点选后→再点空位填入；也可先点空位→再点候选词）：", fontSmall, passageW) + 3;
                int blankBarH = 38;
                int candBarH = 130;
                int statusH = 28;
                int submitBtnH = 38;
                // 先建 RTB 给 BuildPassage 量文本用（后面 recreate）
                int passageMaxH = CapH() - titleH - pad - (blankHintH + 1 + blankBarH + 6 + candHintH + 1 + candBarH + 4 + statusH + 6 + submitBtnH) - pad;
                string passageText = BuildPassage();
                int passageRealH = MeasureH(passageText, fontPassage, passageW - 24) + 50;
                int passageH = Math.Min(passageRealH, passageMaxH);
                int formH = titleH + pad + passageH + 6 + blankHintH + 1 + blankBarH + 6 + candHintH + 1 + candBarH + 4 + statusH + 6 + submitBtnH + pad;
                Action ResizeForPassage = null;

                var form = new Form
                {
                    Text = "ToastFish - 15选10 完形填空",
                    Size = new Size(formW, formH),
                    StartPosition = FormStartPosition.Manual,
                    Left = wa.Right - formW - 20,
                    Top = wa.Bottom - formH - 20,
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    TopMost = true,
                    BackColor = Color.FromArgb(32, 32, 48),
                    ForeColor = Color.FromArgb(230, 230, 230)
                };

                // 1. 标题
                int y = pad;
                form.Controls.Add(new Label
                {
                    Text = "15选10 完形填空  —  从下方候选词中选词填入短文空位",
                    Font = fontTitle,
                    ForeColor = Color.FromArgb(255, 200, 100),
                    AutoSize = true,
                    Location = new Point(pad, y)
                });
                y += titleH;

                // 2. 短文 RichTextBox
                rtb = new RichTextBox
                {
                    Location = new Point(pad, y),
                    Size = new Size(passageW, passageH),
                    Font = fontPassage,
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.FromArgb(31, 31, 40),
                    ForeColor = Color.FromArgb(210, 210, 210),
                    ScrollBars = (passageRealH > passageMaxH) ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None,
                    TabStop = false,
                    DetectUrls = false,
                    WordWrap = true
                };
                rtb.RightMargin = passageW - 24;
                form.Controls.Add(rtb);
                AttachWordLookup(rtb);   // 点击任意单词查有道词典（悬停变手型）
                y += passageH + 6;

                // 3. 空位导航条
                string blankHint = "空位（点选后→再点候选词填入；再点已填空位可撤销）：";
                blankHintH = MeasureH(blankHint, fontSmall, passageW) + 2;
                blankLabel = new Label
                {
                    Text = blankHint,
                    Font = fontSmall,
                    ForeColor = Color.FromArgb(160, 160, 160),
                    AutoSize = false,
                    Size = new Size(passageW, blankHintH),
                    Location = new Point(pad, y)
                };
                form.Controls.Add(blankLabel);
                y += blankHintH + 1;

                blankPanel = new FlowLayoutPanel
                {
                    Location = new Point(pad, y),
                    Size = new Size(passageW, blankBarH),
                    BackColor = Color.FromArgb(28, 28, 42),
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false
                };
                for (int b = 0; b < 10; b++)
                {
                    int bi = b;
                    var btn = new Button
                    {
                        Text = (b + 1).ToString(),
                        Size = new Size(54, 28),
                        BackColor = Color.FromArgb(44, 44, 60),
                        ForeColor = Color.FromArgb(180, 180, 180),
                        FlatStyle = FlatStyle.Flat,
                        Font = fontSmall,
                        Margin = new Padding(2)
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 100);
                    btn.Click += (s2, e2) =>
                    {
                        if (submitted) return;
                        if (filledWords[bi] != null)
                        {
                            // 撤销填入 — 用存储索引定位
                            int cIdx2 = filledCandIdx[bi];
                            string undo = filledWords[bi];
                            filledWords[bi] = null;
                            filledCandIdx[bi] = -1;
                            filledCount--;
                            if (cIdx2 >= 0 && cIdx2 < candBtns.Count && usedCandidates.Contains(cIdx2))
                            {
                                usedCandidates.Remove(cIdx2);
                                var cb = candBtns[cIdx2];
                                cb.BackColor = Color.FromArgb(44, 44, 64);
                                cb.ForeColor = Color.FromArgb(200, 220, 255);
                                cb.Enabled = true;
                            }
                            ClearSelection();
                        }
                        else
                        {
                            // 选中此空位
                            selBlank = (selBlank == bi) ? -1 : bi;
                        }
                        RenderPassage();
                        ResizeForPassage();
                        RefreshBlankBtns();
                        RefreshSubmitBtn();
                        UpdateSubtitle();
                    };
                    blankPanel.Controls.Add(btn);
                    blankBtns[b] = btn;
                }
                form.Controls.Add(blankPanel);
                y += blankBarH + 6;

                // 4. 候选词按钮
                string candHint = "候选词（点选后→再点空位填入；也可先点空位→再点候选词）：";
                candHintH = MeasureH(candHint, fontSmall, passageW) + 2;
                candLabel = new Label
                {
                    Text = candHint,
                    Font = fontSmall,
                    ForeColor = Color.FromArgb(160, 160, 160),
                    AutoSize = false,
                    Size = new Size(passageW, candHintH),
                    Location = new Point(pad, y)
                };
                form.Controls.Add(candLabel);
                y += candHintH + 1;

                candPanel = new FlowLayoutPanel
                {
                    Location = new Point(pad, y),
                    Size = new Size(passageW, candBarH),
                    AutoScroll = true,
                    BackColor = Color.FromArgb(28, 28, 42),
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true
                };
                for (int i = 0; i < cloze.Candidates.Count; i++)
                {
                    int ci = i;
                    string wrd = cloze.Candidates[i].Word;
                    int bw = Math.Max(68, TextRenderer.MeasureText(wrd, fontWord).Width + 16);
                    var btn = new Button
                    {
                        Text = wrd,
                        Size = new Size(bw, 34),
                        BackColor = Color.FromArgb(44, 44, 64),
                        ForeColor = Color.FromArgb(200, 220, 255),
                        FlatStyle = FlatStyle.Flat,
                        Font = fontWord,
                        Margin = new Padding(3)
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 120);
                    btn.Click += (s2, e2) =>
                    {
                        if (submitted) return;
                        if (usedCandidates.Contains(ci)) return;

                        // 找到目标空位
                        int target = selBlank;
                        if (target < 0 || filledWords[target] != null)
                        {
                            // 无有效选中空位 → 找第一个未填的
                            target = -1;
                            for (int b = 0; b < 10; b++)
                            { if (filledWords[b] == null) { target = b; break; } }
                        }
                        if (target < 0) return;

                        // 填空
                        filledWords[target] = wrd;
                        filledCandIdx[target] = ci;
                        usedCandidates.Add(ci);
                        btn.BackColor = Color.FromArgb(60, 60, 60);
                        btn.ForeColor = Color.FromArgb(100, 100, 100);
                        btn.Enabled = false;
                        filledCount++;
                        ClearSelection();

                        RenderPassage();
                        ResizeForPassage();
                        RefreshBlankBtns();
                        RefreshSubmitBtn();
                        UpdateSubtitle();
                    };
                    candPanel.Controls.Add(btn);
                    candBtns.Add(btn);
                }
                form.Controls.Add(candPanel);
                y += candBarH + 4;

                // 5. 状态栏
                subtitleLbl = new Label
                {
                    Text = "已填: 0/" + correctWordCount,
                    Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                    ForeColor = Color.FromArgb(200, 180, 120),
                    AutoSize = true,
                    Location = new Point(pad, y)
                };
                form.Controls.Add(subtitleLbl);

                translateBtn = new Button
                {
                    Text = "显示翻译",
                    Size = new Size(80, 24),
                    Location = new Point(passageW - 70, y - 2),
                    BackColor = Color.FromArgb(50, 50, 70),
                    ForeColor = Color.FromArgb(180, 180, 200),
                    FlatStyle = FlatStyle.Flat,
                    Font = fontSmall
                };
                translateBtn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 110);
                translateBtn.Click += (s2, e2) =>
                {
                    if (translateLbl == null)
                    {
                        // 翻译高度 = min(测量高, 75% 上界内可用区)；超限时翻译区自身滚动，保证不越出窗体底
                        int measured = TextRenderer.MeasureText(cloze.TextCN ?? "", fontCN,
                            new Size(passageW - 10, 9999),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 10;
                        int available = CapH() - (submitBtn.Top + submitBtnH + 6 + pad);
                        int transH = Math.Max(60, Math.Min(measured, available));
                        translateLbl = new RichTextBox
                        {
                            Text = cloze.TextCN ?? "",
                            Font = fontCN,
                            ForeColor = Color.FromArgb(160, 160, 180),
                            BackColor = Color.FromArgb(32, 32, 48),
                            BorderStyle = BorderStyle.None,
                            ReadOnly = true,
                            DetectUrls = false,
                            ScrollBars = measured > available ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None,
                            Size = new Size(passageW - 10, transH),
                            Location = new Point(pad, submitBtn.Top + submitBtnH + 6)
                        };
                        form.Controls.Add(translateLbl);
                        int newH = Math.Min(submitBtn.Top + submitBtnH + 6 + transH + pad, CapH());
                        form.Height = newH;
                        form.Top = Math.Max(0, wa.Bottom - newH - 20);
                    }
                    else
                    {
                        form.Controls.Remove(translateLbl);
                        translateLbl.Dispose();
                        translateLbl = null;
                        // 恢复到自适应高度
                        int newH = submitBtn.Top + submitBtnH + pad;
                        form.Height = Math.Min(newH, CapH());
                        form.Top = Math.Max(0, wa.Bottom - form.Height - 20);
                    }
                };
                form.Controls.Add(translateBtn);

                // 6. 提交按钮
                submitBtn = new Button
                {
                    Text = "提交答案（已填 0 / 需≥" + minToSubmit + "）",
                    Size = new Size(passageW, submitBtnH),
                    Location = new Point(pad, y + statusH + 6),
                    BackColor = Color.FromArgb(60, 60, 60),
                    ForeColor = Color.FromArgb(140, 140, 140),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                    Enabled = false
                };
                submitBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 100);
                submitBtn.Click += (s2, e2) =>
                {
                    if (submitted)
                    {
                        // 第二次点击 → 关闭
                        int corr = 0;
                        for (int b = 0; b < 10; b++)
                            if (filledWords[b] != null && filledWords[b] == correctWords[b]) corr++;
                        clTimer?.Stop(); clTimer?.Dispose();
                        if (_current == form) _current = null;
                        _isHidden = false;
                        form.Close(); form.Dispose();
                        callback((correctWordCount << 16) | corr);
                    }
                    else if (filledCount >= minToSubmit)
                    {
                        // 提交 → 显示结果
                        submitted = true;
                        selBlank = -1;
                        // 禁用所有候选词按钮
                        foreach (var cb in candBtns) { cb.Enabled = false; }
                        // 显示得分
                        int corr = 0;
                        for (int b = 0; b < 10; b++)
                        {
                            int idx = filledCandIdx[b];
                            if (idx >= 0 && idx < cloze.Candidates.Count && cloze.Candidates[idx].Blank == b)
                                corr++;
                        }
                        RenderPassage();
                        ResizeForPassage();
                        RefreshBlankBtns();
                        RefreshSubmitBtn();
                        UpdateSubtitle();
                        // 自动显示翻译（提交按钮下方）
                        if (translateLbl == null && !string.IsNullOrEmpty(cloze.TextCN))
                        {
                            int measured = TextRenderer.MeasureText(cloze.TextCN ?? "", fontCN,
                                new Size(passageW - 10, 9999),
                                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 10;
                            int available = CapH() - (submitBtn.Top + submitBtnH + 6 + pad);
                            int h = Math.Max(60, Math.Min(measured, available));
                            translateLbl = new RichTextBox
                            {
                                Text = cloze.TextCN ?? "",
                                Font = fontCN,
                                ForeColor = Color.FromArgb(160, 160, 180),
                                BackColor = Color.FromArgb(32, 32, 48),
                                BorderStyle = BorderStyle.None,
                                ReadOnly = true,
                                DetectUrls = false,
                                ScrollBars = measured > available ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None,
                                Size = new Size(passageW - 10, h),
                                Location = new Point(pad, submitBtn.Top + submitBtnH + 8)
                            };
                            form.Controls.Add(translateLbl);
                            int newH = Math.Min(submitBtn.Top + submitBtnH + 6 + h + pad, CapH());
                            form.Height = newH;
                            form.Top = Math.Max(0, wa.Bottom - newH - 20);
                        }
                    }
                };
                form.Controls.Add(submitBtn);

                // 7. 初始渲染（先用初始 formH 渲染，ResizeForPassage 再精调）
                ResizeForPassage = () =>
                {
                    // 同步取真实渲染高（GetPositionFromCharIndex，需句柄已创建；ContentsResized 异步不可靠）
                    int realH = RealRtbHeight(rtb);
                    int useH = Math.Min(realH, passageMaxH);
                    rtb.Height = useH;
                    rtb.ScrollBars = (realH > passageMaxH) ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None;
                    int ny = rtb.Top + useH + 6;
                    blankLabel.Top = ny;
                    blankPanel.Top = ny + blankHintH + 1;
                    candLabel.Top = blankPanel.Top + blankBarH + 6;
                    candPanel.Top = candLabel.Top + candHintH + 1;
                    subtitleLbl.Top = candPanel.Top + candBarH + 4;
                    translateBtn.Top = subtitleLbl.Top - 2;
                    submitBtn.Top = subtitleLbl.Top + statusH + 6;
                    // 窗体高度 = 提交按钮底；若翻译已显示，追加翻译高度（其高度已在创建时夹紧到 75% 内）
                    int bottomH = submitBtn.Top + submitBtnH + pad;
                    if (translateLbl != null)
                    {
                        translateLbl.Top = submitBtn.Top + submitBtnH + 6;
                        bottomH = submitBtn.Top + submitBtnH + 6 + translateLbl.Height + pad;
                    }
                    form.Height = Math.Min(bottomH, CapH());
                    form.Top = Math.Max(0, wa.Bottom - form.Height - 20);
                };
                // 前置句柄：ResizeForPassage 用 GetPositionFromCharIndex 同步取真高，必须先建句柄
                form.CreateControl();
                RenderPassage();
                ResizeForPassage();
                RefreshBlankBtns();
                RefreshSubmitBtn();

                // 8. 超时
                if (timeoutMs > 0)
                {
                    clTimer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                    clTimer.Tick += (s2, e2) =>
                    {
                        clTimer.Stop();
                        int corr = 0;
                        int att = filledCount;
                        for (int b = 0; b < 10; b++)
                            if (filledWords[b] != null && filledWords[b] == correctWords[b]) corr++;
                        if (_current == form) _current = null;
                        _isHidden = false;
                        form.Close(); form.Dispose();
                        callback((att << 16) | corr);
                    };
                    clTimer.Start();
                    form.Tag = new PopupState { Timer = clTimer, TotalMs = timeoutMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };
                }

                // 9. 清理
                form.FormClosed += (s2, e2) =>
                {
                    clTimer?.Dispose();
                    if (_current == form) _current = null;
                    _isHidden = false;
                    form.Dispose();
                };

                _isHidden = false;
                _current = form;
                form.Show();
            }));
        }

        /// <summary>
        /// 考研英语 20空四选一 完形填空弹窗（2026-09-05）。
        /// 交互：点空位 → 底部显示该空的 4 个选项 → 点选项填入；全部填完（≥minToSubmit）提交计分。
        /// callback((attempted&lt;&lt;16) | correct)：attempted=已填数，correct=答对数。
        /// </summary>
        public static void ShowCloze4ChoicePopup(Cloze4Result cloze, List<string> highlightWords,
            Action<int> callback, int timeoutMs = TIMEOUT_READING)
        {
            var dispatcher = Model.PushControl.PushWords.UIDispatcher;
            if (dispatcher == null) { callback(0); return; }

            dispatcher.BeginInvoke(new Action(() =>
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                int pad = 10;
                int cw = Math.Min(680, wa.Width - 40);
                int formW = cw;
                int passageW = cw - 2 * pad;

                var fontPassage = new Font("Microsoft YaHei UI", 12, FontStyle.Regular);
                var fontPassageB = new Font("Microsoft YaHei UI", 12, FontStyle.Bold);
                var fontWord = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);
                var fontSmall = new Font("Microsoft YaHei UI", 9, FontStyle.Regular);
                var fontTitle = new Font("Microsoft YaHei UI", 13, FontStyle.Bold);
                var fontCN = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);

                int blankCount = cloze.Questions.Count; // 实际有效空位数（≥15）
                int minToSubmit = Math.Max(blankCount - 5, 12);

                // === 提前声明闭包变量 ===
                RichTextBox rtb = null;
                Button submitBtn = null;
                Label subtitleLbl = null;
                Label blankLabel = null; Label optLabel = null;
                FlowLayoutPanel blankPanel = null; FlowLayoutPanel optPanel = null;
                int blankHintH = 0; int optHintH = 0;
                System.Windows.Forms.Timer clTimer = null;
                Button[] blankBtns = new Button[20];
                Button[] optBtns = new Button[4];
                string[] filledWords = new string[20];
                string[] correctWords = new string[20];
                var blank2opt = new Dictionary<int, string[]>();   // 空位下标(0~19) → 4 选项
                int filledCount = 0;
                int selBlank = -1;
                bool submitted = false;

                // 从 questions 提取 correctWords + 选项（blank 1~20 → 下标 0~19）
                foreach (var q in cloze.Questions)
                {
                    int bi = q.Blank - 1;
                    if (bi < 0 || bi >= 20) continue;
                    if (q.Answer >= 0 && q.Answer < q.Options.Count)
                        correctWords[bi] = q.Options[q.Answer];
                    blank2opt[bi] = q.Options.ToArray();
                }

                // 构造段落文本：把 [N] 替换为可视化空位
                string BuildPassage()
                {
                    string t = cloze.Text;
                    for (int i = 20; i >= 1; i--)
                    {
                        string marker = "[" + i + "]";
                        string repl;
                        if (filledWords[i - 1] != null)
                            repl = " ▶" + filledWords[i - 1] + "◀ ";
                        else if (selBlank == i - 1)
                            repl = " ┌_" + i + "_┐ ";
                        else
                            repl = " ___" + i + "___ ";
                        int idx = t.IndexOf(marker);
                        if (idx >= 0)
                            t = t.Substring(0, idx) + repl + t.Substring(idx + marker.Length);
                    }
                    return t;
                }

                void RenderPassage()
                {
                    rtb.SuspendLayout();
                    string fullText = BuildPassage();
                    rtb.Text = fullText;
                    rtb.SelectAll();
                    rtb.SelectionFont = fontPassage;
                    rtb.SelectionColor = Color.FromArgb(210, 210, 210);
                    for (int i = 0; i < 20; i++)
                    {
                        string search;
                        Color clr;
                        if (filledWords[i] != null)
                        { search = "▶" + filledWords[i] + "◀"; clr = Color.FromArgb(0, 255, 136); }
                        else if (selBlank == i)
                        { search = "┌_" + (i + 1) + "_┐"; clr = Color.FromArgb(255, 200, 100); }
                        else
                        { search = "___" + (i + 1) + "___"; clr = Color.FromArgb(255, 180, 60); }
                        int pos = rtb.Text.IndexOf(search);
                        if (pos >= 0)
                        {
                            rtb.Select(pos, search.Length);
                            rtb.SelectionFont = fontPassageB;
                            rtb.SelectionColor = clr;
                        }
                    }
                    rtb.Select(0, 0);
                    rtb.ResumeLayout();
                }

                void RefreshBlankBtns()
                {
                    for (int b = 0; b < 20; b++)
                    {
                        bool hasBlank = blank2opt.ContainsKey(b);
                        blankBtns[b].Visible = hasBlank;
                        if (!hasBlank) continue;
                        if (filledWords[b] != null)
                        {
                            blankBtns[b].Text = (b + 1) + ". " + filledWords[b];
                            blankBtns[b].BackColor = submitted
                                ? (filledWords[b] == correctWords[b] ? Color.FromArgb(0, 90, 50) : Color.FromArgb(90, 20, 40))
                                : Color.FromArgb(0, 70, 50);
                            blankBtns[b].ForeColor = submitted
                                ? (filledWords[b] == correctWords[b] ? Color.FromArgb(0, 255, 136) : Color.FromArgb(255, 80, 110))
                                : Color.FromArgb(0, 255, 136);
                        }
                        else
                        {
                            blankBtns[b].Text = (b + 1).ToString();
                            if (selBlank == b && !submitted)
                            {
                                blankBtns[b].BackColor = Color.FromArgb(80, 60, 20);
                                blankBtns[b].ForeColor = Color.FromArgb(255, 200, 100);
                            }
                            else
                            {
                                blankBtns[b].BackColor = Color.FromArgb(44, 44, 60);
                                blankBtns[b].ForeColor = Color.FromArgb(180, 180, 180);
                            }
                        }
                    }
                }

                // 刷新选项区：显示当前选中空位的 4 个选项
                void RefreshOptions()
                {
                    string[] opts = (selBlank >= 0 && blank2opt.ContainsKey(selBlank)) ? blank2opt[selBlank] : null;
                    for (int i = 0; i < 4; i++)
                    {
                        if (opts != null && i < opts.Length)
                        {
                            optBtns[i].Text = ((char)('A' + i)) + ". " + opts[i];
                            optBtns[i].Visible = true;
                            optBtns[i].Enabled = !submitted;
                        }
                        else
                        {
                            optBtns[i].Visible = false;
                        }
                    }
                }

                void RefreshSubmitBtn()
                {
                    if (submitted)
                    {
                        int corr = 0;
                        for (int b = 0; b < 20; b++)
                            if (filledWords[b] != null && filledWords[b] == correctWords[b]) corr++;
                        submitBtn.Text = "关闭（答对 " + corr + " / " + blankCount + "）";
                        submitBtn.BackColor = Color.FromArgb(70, 90, 60);
                        submitBtn.ForeColor = Color.FromArgb(220, 255, 220);
                        submitBtn.Enabled = true;
                    }
                    else if (filledCount >= minToSubmit)
                    {
                        submitBtn.Text = "✓ 提交答案（已填 " + filledCount + " / 需≥" + minToSubmit + "）";
                        submitBtn.BackColor = Color.FromArgb(80, 60, 30);
                        submitBtn.ForeColor = Color.FromArgb(255, 220, 140);
                        submitBtn.Enabled = true;
                    }
                    else
                    {
                        submitBtn.Text = "提交答案（已填 " + filledCount + " / 需≥" + minToSubmit + "）";
                        submitBtn.BackColor = Color.FromArgb(60, 60, 60);
                        submitBtn.ForeColor = Color.FromArgb(140, 140, 140);
                        submitBtn.Enabled = false;
                    }
                }

                void UpdateSubtitle()
                {
                    subtitleLbl.Text = submitted
                        ? "已完成 — 绿色=答对，红色=答错"
                        : "已填: " + filledCount + " / " + blankCount;
                }

                // 布局常量（提前计算提示文本高度，避免 form 构造时引用未赋值的变量）
                blankHintH = MeasureH("空位（点击选中，再在下方选项中选择）：", fontSmall, passageW) + 2;
                optHintH = MeasureH("选项（A/B/C/D，点击即填入上方选中的空位）：", fontSmall, passageW) + 2;
                int titleH = MeasureH("20空四选一 完形填空", fontTitle, passageW) + 6;
                int blankBarH = 64;   // 20 个空位按钮（约 2 行）
                int optBarH = 44;     // 4 个选项按钮（1 行）
                int statusH = 26;
                int submitBtnH = 40;
                int passageH = (int)(wa.Height * 0.42);   // 短文区占 42% 屏高（300 词短文足够，超限出滚动条）
                int formH = pad + titleH + passageH + 6 + blankHintH + 1 + blankBarH + 6 + optHintH + 1 + optBarH + 4 + statusH + 6 + submitBtnH + pad;
                formH = Math.Min(formH, CapH());

                int y = pad;
                var form = new Form
                {
                    Width = formW,
                    Height = formH,
                    Left = wa.Right - formW - 20,
                    Top = wa.Bottom - formH - 20,
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    TopMost = true,
                    BackColor = Color.FromArgb(32, 32, 48),
                    ForeColor = Color.FromArgb(230, 230, 230)
                };

                // 1. 标题
                form.Controls.Add(new Label
                {
                    Text = "20空四选一 完形填空  —  点击空位，从 A/B/C/D 中选择",
                    Font = fontTitle,
                    ForeColor = Color.FromArgb(255, 200, 100),
                    AutoSize = true,
                    Location = new Point(pad, y)
                });
                y += titleH;

                // 2. 短文 RichTextBox
                rtb = new RichTextBox
                {
                    Location = new Point(pad, y),
                    Size = new Size(passageW, passageH),
                    Font = fontPassage,
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    BackColor = Color.FromArgb(31, 31, 40),
                    ForeColor = Color.FromArgb(210, 210, 210),
                    ScrollBars = RichTextBoxScrollBars.Vertical,
                    TabStop = false,
                    DetectUrls = false,
                    WordWrap = true
                };
                rtb.RightMargin = passageW - 24;
                form.Controls.Add(rtb);
                AttachWordLookup(rtb);
                y += passageH + 6;

                // 3. 空位导航条（20 个按钮）
                string blankHint = "空位（点击选中，再在下方选项中选择）：";
                blankLabel = new Label
                {
                    Text = blankHint,
                    Font = fontSmall,
                    ForeColor = Color.FromArgb(160, 160, 160),
                    AutoSize = false,
                    Size = new Size(passageW, blankHintH),
                    Location = new Point(pad, y)
                };
                form.Controls.Add(blankLabel);
                y += blankHintH + 1;

                blankPanel = new FlowLayoutPanel
                {
                    Location = new Point(pad, y),
                    Size = new Size(passageW, blankBarH),
                    BackColor = Color.FromArgb(28, 28, 42),
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoScroll = true
                };
                for (int b = 0; b < 20; b++)
                {
                    int bi = b;
                    var btn = new Button
                    {
                        Text = (b + 1).ToString(),
                        Size = new Size(60, 26),
                        BackColor = Color.FromArgb(44, 44, 60),
                        ForeColor = Color.FromArgb(180, 180, 180),
                        FlatStyle = FlatStyle.Flat,
                        Font = fontSmall,
                        Margin = new Padding(2)
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 100);
                    btn.Click += (s2, e2) =>
                    {
                        if (submitted) return;
                        if (!blank2opt.ContainsKey(bi)) return;
                        // 选中/切换此空位
                        selBlank = (selBlank == bi) ? -1 : bi;
                        RenderPassage();
                        RefreshBlankBtns();
                        RefreshOptions();
                        UpdateSubtitle();
                    };
                    blankPanel.Controls.Add(btn);
                    blankBtns[b] = btn;
                }
                form.Controls.Add(blankPanel);
                y += blankBarH + 6;

                // 4. 选项区（4 个选项按钮，随选中空位变化）
                string optHint = "选项（A/B/C/D，点击即填入上方选中的空位）：";
                optLabel = new Label
                {
                    Text = optHint,
                    Font = fontSmall,
                    ForeColor = Color.FromArgb(160, 160, 160),
                    AutoSize = false,
                    Size = new Size(passageW, optHintH),
                    Location = new Point(pad, y)
                };
                form.Controls.Add(optLabel);
                y += optHintH + 1;

                optPanel = new FlowLayoutPanel
                {
                    Location = new Point(pad, y),
                    Size = new Size(passageW, optBarH),
                    BackColor = Color.FromArgb(28, 28, 42),
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false
                };
                for (int i = 0; i < 4; i++)
                {
                    int oi = i;
                    var btn = new Button
                    {
                        Text = "",
                        Size = new Size((passageW - 20) / 4, 36),
                        BackColor = Color.FromArgb(44, 44, 64),
                        ForeColor = Color.FromArgb(200, 220, 255),
                        FlatStyle = FlatStyle.Flat,
                        Font = fontWord,
                        Margin = new Padding(2),
                        Visible = false
                    };
                    btn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 120);
                    btn.Click += (s2, e2) =>
                    {
                        if (submitted) return;
                        if (selBlank < 0 || !blank2opt.ContainsKey(selBlank)) return;
                        string[] opts = blank2opt[selBlank];
                        if (oi >= opts.Length) return;
                        if (filledWords[selBlank] == null) filledCount++;
                        filledWords[selBlank] = opts[oi];
                        selBlank = -1;
                        RenderPassage();
                        RefreshBlankBtns();
                        RefreshOptions();
                        RefreshSubmitBtn();
                        UpdateSubtitle();
                    };
                    optPanel.Controls.Add(btn);
                    optBtns[i] = btn;
                }
                form.Controls.Add(optPanel);
                y += optBarH + 4;

                // 5. 状态栏
                subtitleLbl = new Label
                {
                    Text = "已填: 0/" + blankCount,
                    Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                    ForeColor = Color.FromArgb(200, 180, 120),
                    AutoSize = true,
                    Location = new Point(pad, y)
                };
                form.Controls.Add(subtitleLbl);

                // 6. 提交按钮
                submitBtn = new Button
                {
                    Text = "提交答案（已填 0 / 需≥" + minToSubmit + "）",
                    Size = new Size(passageW, submitBtnH),
                    Location = new Point(pad, y + statusH + 6),
                    BackColor = Color.FromArgb(60, 60, 60),
                    ForeColor = Color.FromArgb(140, 140, 140),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                    Enabled = false
                };
                submitBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 100);
                submitBtn.Click += (s2, e2) =>
                {
                    if (submitted)
                    {
                        int corr = 0;
                        for (int b = 0; b < 20; b++)
                            if (filledWords[b] != null && filledWords[b] == correctWords[b]) corr++;
                        clTimer?.Stop(); clTimer?.Dispose();
                        if (_current == form) _current = null;
                        _isHidden = false;
                        form.Close(); form.Dispose();
                        callback((filledCount << 16) | corr);
                    }
                    else if (filledCount >= minToSubmit)
                    {
                        submitted = true;
                        selBlank = -1;
                        RefreshOptions();
                        RenderPassage();
                        RefreshBlankBtns();
                        RefreshSubmitBtn();
                        UpdateSubtitle();
                    }
                };
                form.Controls.Add(submitBtn);

                // 7. 初始渲染
                form.CreateControl();
                RenderPassage();
                RefreshBlankBtns();
                RefreshOptions();
                RefreshSubmitBtn();

                // 8. 超时
                if (timeoutMs > 0)
                {
                    clTimer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                    clTimer.Tick += (s2, e2) =>
                    {
                        clTimer.Stop();
                        int corr = 0;
                        int att = filledCount;
                        for (int b = 0; b < 20; b++)
                            if (filledWords[b] != null && filledWords[b] == correctWords[b]) corr++;
                        if (_current == form) _current = null;
                        _isHidden = false;
                        form.Close(); form.Dispose();
                        callback((att << 16) | corr);
                    };
                    clTimer.Start();
                    form.Tag = new PopupState { Timer = clTimer, TotalMs = timeoutMs, StartedAt = DateTime.Now, PausedRemainingMs = -1 };
                }

                // 9. 清理
                form.FormClosed += (s2, e2) =>
                {
                    clTimer?.Dispose();
                    if (_current == form) _current = null;
                    _isHidden = false;
                    form.Dispose();
                };

                _isHidden = false;
                _current = form;
                form.Show();
            }));
        }
    }
}
