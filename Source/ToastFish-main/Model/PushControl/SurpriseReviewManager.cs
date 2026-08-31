using System;
using System.Collections.Generic;
using System.Threading;
using ToastFish.Model.SM2plus;
using ToastFish.Model.SqliteControl;
using ToastFish.View;

namespace ToastFish.Model.PushControl
{
    /// <summary>
    /// 惊喜复习管理器 —— 通过全局输入钩子监控用户操作，
    /// 每次键盘按键或鼠标点击时以极低概率弹出复习弹窗
    /// </summary>
    public static class SurpriseReviewManager
    {
        // ============ 可调配置 ============

        /// <summary>
        /// 每次输入事件的触发概率（0.005 = 0.5%）
        /// 按平均每秒 5 键计算，约每 40 秒触发一次
        /// </summary>
        private const double SURPRISE_PROBABILITY = 0.005;      // 0.5%

        /// <summary>弹窗超时（毫秒）</summary>
        public const int SURPRISE_TIMEOUT_MS = 60000;            // 1 分钟

        // ============ 状态 ============
        private static readonly Random _rng = new Random();
        private static readonly object _rngLock = new object();
        private static volatile bool _surpriseInProgress;
        private static IDisposable _currentSubscription;

        /// <summary>是否有惊喜弹窗正在显示（供外部互斥检查）</summary>
        public static bool IsSurpriseInProgress
        {
            get { return _surpriseInProgress; }
        }

        // ============ 启动 / 停止 ============

        /// <summary>
        /// 安装全局输入钩子并开始监控。应在程序初始化时调用一次。
        /// </summary>
        public static void Start()
        {
            InputMonitor.OnInputEvent += OnUserInput;
            InputMonitor.Start();
            GamepadMonitor.OnInputEvent += OnUserInput;
            GamepadMonitor.Start();
            System.Diagnostics.Debug.WriteLine(
                "SurpriseReview: 已启动 概率={0:F2}% (键盘/鼠标 + 手柄)",
                SURPRISE_PROBABILITY * 100);
        }

        /// <summary>
        /// 停止监控并关闭活跃弹窗。应在程序退出时调用。
        /// </summary>
        public static void Stop()
        {
            InputMonitor.OnInputEvent -= OnUserInput;
            InputMonitor.Stop();
            GamepadMonitor.OnInputEvent -= OnUserInput;
            GamepadMonitor.Stop();
            NotificationForm.CloseCurrent();
            System.Diagnostics.Debug.WriteLine("SurpriseReview: 已停止");
        }

        // ============ 输入事件回调（钩子线程，必须极快返回） ============

        private static void OnUserInput()
        {
            // 1. 学习会话互斥
            if (PushWords.IsLearningActive)
                return;

            // 2. 防重入
            if (_surpriseInProgress)
                return;

            // 3. 概率 roll（_rng 非线程安全，钩子线程 + 手柄定时器线程并发访问）
            double roll;
            lock (_rngLock) { roll = _rng.NextDouble(); }
            if (roll >= SURPRISE_PROBABILITY)
                return;

            // 4. 设置重入锁
            _surpriseInProgress = true;

            System.Diagnostics.Debug.WriteLine("SurpriseReview: 触发！");

            // 5. 切到 UI 线程执行
            var dispatcher = PushWords.UIDispatcher;
            if (dispatcher == null)
            {
                _surpriseInProgress = false;
                return;
            }
            dispatcher.BeginInvoke(new Action(ExecuteSurpriseReview));
        }

        // ============ 核心：执行惊喜复习（UI 线程） ============

        private static void ExecuteSurpriseReview()
        {
            Select select = null;
            try
            {
                // 双重检查（UI 线程）
                if (PushWords.IsLearningActive)
                {
                    _surpriseInProgress = false;
                    return;
                }

                // 1. 打开数据库，加载词库
                select = new Select();
                select.SelectWordList();

                // 2. 加权随机选 1 张已复习卡片
                select.GetOverdueReviewedCardList(1, out List<Card> cards);
                if (cards.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("SurpriseReview: 无已学卡片，跳过");
                    _surpriseInProgress = false;
                    select.Dispose();
                    return;
                }
                Card card = cards[0];

                // 3. 获取 2 个干扰词（优先同词性已学词 → 已学词 → 全库，排除目标词自身）
                List<Word> distractors = select.GetRandomReviewableWords(
                    2, card.word.wordRank, card.word.pos);
                if (distractors.Count < 2)
                {
                    System.Diagnostics.Debug.WriteLine("SurpriseReview: 干扰词不足，跳过");
                    _surpriseInProgress = false;
                    select.Dispose();
                    return;
                }

                // 4. 随机选择题型
                bool wordToDef = _rng.Next(2) == 0;
                string question;
                string[] choices = new string[3];
                int correctIdx = _rng.Next(3);

                if (wordToDef)
                {
                    question = card.word.headWord;
                    choices[correctIdx] = card.word.tranCN;
                    int d = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        if (i == correctIdx) continue;
                        choices[i] = distractors[d].tranCN;
                        d++;
                    }
                }
                else
                {
                    question = card.word.tranCN;
                    choices[correctIdx] = card.word.headWord;
                    int d = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        if (i == correctIdx) continue;
                        choices[i] = distractors[d].headWord;
                        d++;
                    }
                }

                // 5. 选项前缀
                string[] prefixes = { "A", "B", "C" };
                string[] displayChoices = new string[3];
                for (int i = 0; i < 3; i++)
                    displayChoices[i] = prefixes[i] + ". " + choices[i];

                // 6. HotKey 订阅
                int capturedCorrectIdx = correctIdx;
                Card capturedCard = card;
                Select capturedSelect = select;
                bool answerHandled = false;
                object lockObj = new object();

                _currentSubscription = PushWords.HotKeytObservable.Subscribe(events =>
                {
                    int ansIdx = -1;
                    switch (events)
                    {
                        case "1": ansIdx = 0; break;
                        case "2": ansIdx = 1; break;
                        case "3": ansIdx = 2; break;
                        default: return;  // "4" / "S" 不参与惊喜答题
                    }

                    lock (lockObj)
                    {
                        if (answerHandled) return;
                        answerHandled = true;
                    }

                    _currentSubscription?.Dispose();
                    _currentSubscription = null;
                    NotificationForm.CloseCurrent();

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        ProcessAnswer(capturedCard, capturedSelect, ansIdx, capturedCorrectIdx);
                    });
                });

                // 7. 显示弹窗
                string title = wordToDef ? "惊喜复习 · 选释义" : "惊喜复习 · 选单词";
                NotificationForm.ShowSurpriseQuizPopup(
                    title, question, displayChoices,
                    answerIdx =>
                    {
                        lock (lockObj)
                        {
                            if (answerHandled) return;
                            answerHandled = true;
                        }

                        _currentSubscription?.Dispose();
                        _currentSubscription = null;

                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            if (answerIdx >= 0 && answerIdx <= 2)
                                ProcessAnswer(capturedCard, capturedSelect, answerIdx, capturedCorrectIdx);
                            else
                                ProcessTimeout(capturedCard, capturedSelect);
                        });
                    },
                    SURPRISE_TIMEOUT_MS);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SurpriseReview: ExecuteSurpriseReview 异常: " + ex.Message);
                _surpriseInProgress = false;
                try { _currentSubscription?.Dispose(); } catch { }
                _currentSubscription = null;
                try { select?.Dispose(); } catch { }
            }
        }

        // ============ 答案处理（线程池线程） ============

        private static void ProcessAnswer(Card card, Select select, int userChoice, int correctIdx)
        {
            try
            {
                bool correct = (userChoice == correctIdx);
                double score = correct ? Parameters.Good : Parameters.Again;

                System.Diagnostics.Debug.WriteLine("SurpriseReview: 用户选择={0} 正确={1} → score={2}",
                    userChoice, correctIdx, score);

                card.updateCard(score);
                select.updateCardDateBase(new List<Card> { card });

                if (!correct)
                    ShowFailedWordPreview(card.word);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SurpriseReview: ProcessAnswer 异常: " + ex.Message);
            }
            finally
            {
                try { select.Dispose(); } catch { }
                _surpriseInProgress = false;
            }
        }

        private static void ProcessTimeout(Card card, Select select)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("SurpriseReview: 超时 → 视为 Again");
                card.updateCard(Parameters.Again);
                select.updateCardDateBase(new List<Card> { card });
                ShowFailedWordPreview(card.word);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SurpriseReview: ProcessTimeout 异常: " + ex.Message);
            }
            finally
            {
                try { select.Dispose(); } catch { }
                _surpriseInProgress = false;
            }
        }

        /// <summary>
        /// 答错时弹出 3 秒单词预览弹窗 —— 与学习弹窗同布局，无互动按钮
        /// </summary>
        private static void ShowFailedWordPreview(Word w)
        {
            try
            {
                string phonemeStr = Select.ENG_TYPE == 1 ? w.usPhone : w.ukPhone;
                string phoneme = string.IsNullOrEmpty(phonemeStr) ? "" : "  /" + phonemeStr + "/";
                string posTran = string.IsNullOrEmpty(w.pos) ? w.tranCN : w.pos + " " + w.tranCN;
                string sentence = (w.sentence != null && w.sentence.Length < 50) ? w.sentence : "";
                string sentenceCN = (w.sentence != null && w.sentence.Length < 50) ? w.sentenceCN : "";
                string phrase = (w.phrase != null) ? w.phrase : "";
                string phraseCN = (w.phraseCN != null) ? w.phraseCN : "";

                string displayText = w.headWord + "‖" + phoneme + "‖" + posTran + "‖" + sentence + "‖" + sentenceCN + "‖" + phrase + "‖" + phraseCN;
                NotificationForm.ShowWordPreviewPopup(displayText + "‖‖" + "惊喜复习 · 回答错误");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SurpriseReview: ShowFailedWordPreview 异常: " + ex.Message);
            }
        }
    }
}
