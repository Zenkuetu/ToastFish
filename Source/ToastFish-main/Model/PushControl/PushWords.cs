using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;
using ToastFish.Model.SqliteControl;
using System.Threading;
using System.Speech.Synthesis;
using ToastFish.Model.Log;
using System.Diagnostics;
using System.Reactive.Subjects;
using ToastFish.Model.SM2plus;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using ToastFish.View;

namespace ToastFish.Model.PushControl
{
    class MyHotObservable : IObservable<string>
    {
        private Subject<string> subject = new Subject<string>();
        string last_event = "";



        public IDisposable Subscribe(IObserver<string> observer)
        {
            return this.subject.Subscribe(observer);
        }

        public void raiseEvent(string events)
        {
            this.subject.OnNext(events);
            last_event = events;
        }

    }


    class PushWords
    {
        // 当前推送单词的状态
        public int WORD_CURRENT_STATUS = 0;  // 背单词时候的状态
        public string WORD_NUMBER_STRING = "";  // 设置的单词数量
        public int QUESTION_CURRENT_RIGHT_ANSWER = -1;  // 当前问题的答案
        public int QUESTION_CURRENT_STATUS = 0;  // 问题的回答状态
        public Dictionary<string, string> AnswerDict = new Dictionary<string, string> {
            {"0","A"},{"1","B"},{"2","C"},{"3","D"}
        };
        public static MyHotObservable HotKeytObservable = new MyHotObservable();
        /// <summary>UI 线程 Dispatcher，后台线程创建通知窗时使用</summary>
        public static System.Windows.Threading.Dispatcher UIDispatcher;
        /// <summary>是否有学习会话正在活跃。惊喜复习系统检查此标志以避免打断学习。</summary>
        public static volatile bool IsLearningActive = false;

        /// <summary>测试模式：本轮学习结束后自动恢复数据库，不保存学习记录。</summary>
        public static volatile bool TestMode = false;

        // === AI 短文预生成（2026-07-21） ===
        /// <summary>后台预生成的 AI 短文结果，新词学完后启动，复习词阶段异步生成。</summary>
        private static EssayPreFetchResult _essayPreFetchResult = null;
        private static Task _essayPreFetchTask = null;
        private static readonly object _essayPreFetchLock = new object();
        // === 15选10 完形填空预生成（2026-07-22） ===
        private static Model.Ai.ClozeResult _clozePreFetchResult = null;
        private static Task _clozePreFetchTask = null;
        private static List<Word> _clozePreFetchWords = null;
        private static string _clozePreFetchError = null;
        // === 20空四选一 完形填空预生成（考研英语，2026-09-05） ===
        private static Model.Ai.Cloze4Result _cloze4PreFetchResult = null;
        private static Task _cloze4PreFetchTask = null;
        private static List<Word> _cloze4PreFetchWords = null;
        private static string _cloze4PreFetchError = null;
        /// <summary>预生成时决定题型，PushMiniReading 据此取用对应结果。</summary>
        private static bool _preFetchIsCloze = false;


        /// <summary>Sigmoid 函数：1/(1+e^(-x))，用于难度→评分的非线性映射</summary>
        private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

        #region 测试模式（学完自动回滚数据库）
        /// <summary>在 Select.DataBase 连接上开启事务，学习线程结束时 ROLLBACK 即可回滚所有 SM2 写入。</summary>
        private static System.Data.SQLite.SQLiteTransaction BeginTestTransaction(Select query)
        {
            try
            {
                return query.DataBase.BeginTransaction();
            }
            catch (Exception ex) { Debug.WriteLine("BeginTestTransaction failed: " + ex.Message); return null; }
        }
        #endregion

        /// <summary>刷新学习仪表盘 HTML（2026-07-22）。失败静默，不影响学习流程。timeoutMs 为等待 Python 的超时。</summary>
        public static void RefreshDashboard(int timeoutMs = 45000)
        {
            try
            {
                string exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                string script = System.IO.Path.Combine(exeDir, "Resources", "generate_dashboard.py");
                script = System.IO.Path.GetFullPath(script);
                if (!System.IO.File.Exists(script)) return;

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "\"" + script + "\"",
                    WorkingDirectory = exeDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return;
                    // 等待最多 timeoutMs 毫秒，超时就杀（AI 模式下的 WAL 积压最多让查询慢几秒）
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        Debug.WriteLine("RefreshDashboard: Python 超时已终止");
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("RefreshDashboard: " + ex.Message); }
        }

        /// <summary>持久化 AI 短文到 EssayLog 表（2026-07-22）。</summary>
        private static void SaveEssayLog(EssayPreFetchResult fetched, int attempted, int correct)
        {
            if (TestMode) return; // 测试模式不走独立连接写入
            try
            {
                var jss = new System.Web.Script.Serialization.JavaScriptSerializer();
                var wordsArr = fetched.EssayWords.Select(w => w.headWord).ToList();
                string wordsJson = jss.Serialize(wordsArr);
                string createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                int modelMode = Model.Ai.AiConfig.ModelMode;

                string questionsJson = "[]";
                if (fetched.Result.Questions != null && fetched.Result.Questions.Count > 0)
                {
                    var qList = fetched.Result.Questions.Select(q => new
                    {
                        q = q.Question,
                        choices = q.Choices,
                        answer = q.Answer
                    }).ToList();
                    questionsJson = jss.Serialize(qList);
                }

                Select.InsertEssayLog(createdAt, Select.TABLE_NAME, wordsArr.Count,
                    wordsJson, fetched.Result.EssayEN,
                    fetched.Result.EssayCN ?? "", questionsJson,
                    attempted, correct, modelMode);
            }
            catch (Exception ex) { Debug.WriteLine("SaveEssayLog: " + ex.Message); }
        }

        /// <summary>持久化 15选10 完形填空到 EssayLog 表（2026-07-22）。</summary>
        private static void SaveClozeLog(List<Word> clozeWords, Model.Ai.ClozeResult cloze,
            int attempted, int correct)
        {
            if (TestMode) return; // 测试模式不走独立连接写入
            try
            {
                var jss = new System.Web.Script.Serialization.JavaScriptSerializer();
                var wordsArr = clozeWords.Select(w => w.headWord).ToList();
                string wordsJson = jss.Serialize(wordsArr);
                string createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                int modelMode = Model.Ai.AiConfig.ModelMode;

                var candidatesData = cloze.Candidates.Select(c => new {
                    word = c.Word,
                    blank = c.Blank
                }).ToList();
                var clozeData = new { type = "cloze", candidates = candidatesData };
                string questionsJson = jss.Serialize(clozeData);

                Select.InsertEssayLog(createdAt, Select.TABLE_NAME, wordsArr.Count,
                    wordsJson, cloze.Text, cloze.TextCN ?? "", questionsJson,
                    attempted, correct, modelMode);
            }
            catch (Exception ex) { Debug.WriteLine("SaveClozeLog: " + ex.Message); }
        }

        /// <summary>持久化 20空四选一 完形填空到 EssayLog 表（考研英语，2026-09-05）。</summary>
        private static void SaveCloze4Log(List<Word> cloze4Words, Model.Ai.Cloze4Result cloze4,
            int attempted, int correct)
        {
            if (TestMode) return; // 测试模式不走独立连接写入
            try
            {
                var jss = new System.Web.Script.Serialization.JavaScriptSerializer();
                var wordsArr = cloze4Words.Select(w => w.headWord).ToList();
                string wordsJson = jss.Serialize(wordsArr);
                string createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                int modelMode = Model.Ai.AiConfig.ModelMode;

                var questionsData = cloze4.Questions.Select(q => new {
                    blank = q.Blank,
                    options = q.Options,
                    answer = q.Answer
                }).ToList();
                var cloze4Data = new { type = "cloze4", questions = questionsData };
                string questionsJson = jss.Serialize(cloze4Data);

                Select.InsertEssayLog(createdAt, Select.TABLE_NAME, wordsArr.Count,
                    wordsJson, cloze4.Text, cloze4.TextCN ?? "", questionsJson,
                    attempted, correct, modelMode);
            }
            catch (Exception ex) { Debug.WriteLine("SaveCloze4Log: " + ex.Message); }
        }

        /// <summary>
        /// 判断字符串是否为数字
        /// </summary>
        public bool IsNumber(string str)
        {
            char[] ch = new char[str.Length];
            ch = str.ToCharArray();
            for (int i = 0; i < ch.Length; i++)
            {
                if (ch[i] < 48 || ch[i] > 57)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 从List中获取一个随机单词
        /// </summary>
        public Word GetRandomWord(List<Word> WordList)
        {
            Random Rd = new Random();
            int Index = Rd.Next(WordList.Count);
            return WordList[Index];
        }

        public List<Word> GetRandomWordLst(Word CurrentWord, List<Word> WordList, int num)
        {
            Debug.Assert(num < WordList.Count);
            Random Rd = new Random();
            List<Word> CopyList = new List<Word>(WordList.ToArray());  // Clone<Word>(WordList);

            int id1 = CopyList.FindIndex(wordi =>
            {
                return (wordi.wordRank == CurrentWord.wordRank);
            });
            if (id1 >= 0)
                CopyList.RemoveAt(id1);

            List<Word> randwordLst = new List<Word>();
            for (int i = 0; i < num; i++)
            {
                int Index = Rd.Next(CopyList.Count);
                randwordLst.Add(CopyList[Index]);
                CopyList.RemoveAt(Index);
            }
            Debug.WriteLine($"copyList.count={CopyList.Count}");
            Debug.WriteLine($"WordList.count={WordList.Count}");
            return randwordLst;
        }

        /// <summary>
        /// 推送单词的Task
        /// </summary>
        public async Task<int> ProcessToastNotificationRecitation()
        {
            var Tcs = new TaskCompletionSource<int>();

            using (HotKeytObservable.Subscribe(events =>
            {
                switch (events)
                {
                    case "1": Tcs.TrySetResult(0); break;
                    case "2": Tcs.TrySetResult(1); break;
                    case "3": Tcs.TrySetResult(2); break;
                }
            }))
            {
                return await Tcs.Task;
            }
        }

        public async Task<int> ProcessToastNotificationRecitationSM2()
        {
            var Tcs = new TaskCompletionSource<int>();

            using (HotKeytObservable.Subscribe(events =>
            {
                switch (events)
                {
                    case "1": Tcs.TrySetResult(1); break;
                    case "2": Tcs.TrySetResult(2); break;
                    case "3": Tcs.TrySetResult(3); break;
                    case "4": Tcs.TrySetResult(4); break;
                    case "S": Tcs.TrySetResult(0); break;
                }
            }))
            {
                return await Tcs.Task;
            }
        }

        /// <summary>
        /// 推送问题的Task
        /// </summary>
        public async Task<int> ProcessToastNotificationQuestion()
        {
            var Tcs = new TaskCompletionSource<int>();

            using (HotKeytObservable.Subscribe(events =>
            {
                int Ans = -1;
                switch (events)
                {
                    case "1": Ans = 0; break;
                    case "2": Ans = 1; break;
                    case "3": Ans = 2; break;
                    case "4": Ans = 3; break;
                }
                if (Ans >= 0)
                    Tcs.TrySetResult(Ans == QUESTION_CURRENT_RIGHT_ANSWER ? 1 : 0);
            }))
            {
                return await Tcs.Task;
            }
        }

        /// <summary>
        /// 设置单词数量的Task
        /// </summary>
        public Task<int> ProcessToastNotificationSetNumber()
        {
            var Tcs = new TaskCompletionSource<int>();

            // 使用 MessageBox 输入代替 UWP Toast 文本输入
            UIDispatcher?.BeginInvoke(new Action(() =>
            {
                string input = Microsoft.VisualBasic.Interaction.InputBox(
                    "这次要背多少个？", "ToastFish - 设置单词数量", "10");
                if (int.TryParse(input, out int num) && num > 0)
                {
                    WORD_NUMBER_STRING = num.ToString();
                    Tcs.TrySetResult(1);
                }
                else
                {
                    Tcs.TrySetResult(0);
                }
            }));

            return Tcs.Task;
        }

        /// <summary>
        /// 设置单词数量
        /// </summary>
        public void SetWordNumber()
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "这次要背多少个？(可选: 5/10/15/20)", "ToastFish - 单词个数",
                Select.WORD_NUMBER.ToString());
            if (int.TryParse(input, out int num) && num > 0)
            {
                Select.WORD_NUMBER = num;
                Select Temp = new Select();
                Temp.UpdateNumber(Select.WORD_NUMBER);
                PushMessage("已设置单词数量为：" + num);
            }
        }

        public void SetEngType()
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "请选择发音类型：\n1 = 美式发音\n2 = 英式发音", "ToastFish - 英标类型",
                Select.ENG_TYPE.ToString());
            if (int.TryParse(input, out int engType) && (engType == 1 || engType == 2))
            {
                Select.ENG_TYPE = engType;
                string rst = engType == 1 ? "美国" : "英国";
                PushMessage("已设置英语类型为：" + rst);
                Select Temp = new Select();
                Temp.UpdateGlobalConfig();
            }
        }

        public double pushCard(Card card, Cardstatus cardstatus, int numNewCards, int numLearingCards, int numReviewedCards)
        {
            Word CurrentWord = card.word;
            int answer;
            double result = -1;
            bool isFinished = false;
            string word_pron, word_save_name;
            switch (Select.ENG_TYPE)
            {
                case 1:
                    word_save_name = CurrentWord.headWord + "_us";
                    word_pron = CurrentWord.headWord + "&type=1";
                    break;
                default:
                    word_save_name = CurrentWord.headWord + "_uk";
                    word_pron = CurrentWord.headWord + "&type=2";
                    break;
            }
            while (isFinished != true)
            {
                // 先弹窗，再播语音
                PushOneWordSM2(CurrentWord, cardstatus, numNewCards, numLearingCards, numReviewedCards);

                if (Select.AUTO_PLAY != 0)
                {
                    // 异步播放，不阻塞弹窗
                    string mp3_fs_name = word_save_name;
                    string mp3_url_param = word_pron;
                    string mp3_headWord = CurrentWord.headWord;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        List<string> mp3List = new List<string>();
                        mp3List.Add(mp3_fs_name);
                        mp3List.Add(mp3_url_param);
                        bool isOK = Download.DownloadMp3.PlayMp3(mp3List);
                        if (isOK == false)
                        {
                            SpeechSynthesizer synth = new SpeechSynthesizer();
                            synth.SpeakAsync(mp3_headWord);
                        }
                    });
                }

                try
                {
                    var task = this.ProcessToastNotificationRecitationSM2();
                    answer = task.Result;
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    return result;
                }
                if (answer == 1)
                {
                    result = Parameters.Again;
                    isFinished = true;
                }
                else if (answer == 2)
                {
                    result = Parameters.Hard;
                    isFinished = true;
                }
                else if (answer == 3)
                {
                    result = Parameters.Good;
                    isFinished = true;
                }
                else if (answer == 4)
                {
                    result = Parameters.Easy;
                    isFinished = true;
                }
                else if (answer == 0)
                {
                    List<string> replayMp3 = new List<string>();
                    replayMp3.Add(word_save_name);
                    replayMp3.Add(word_pron);
                    bool isOK = Download.DownloadMp3.PlayMp3(replayMp3);
                    if (isOK == false)
                    {
                        SpeechSynthesizer synth = new SpeechSynthesizer();
                        synth.SpeakAsync(CurrentWord.headWord);
                    }
                }
            }
            return result;
        }

        public static void RecitationSM2(Object wordtype)
        {
            // 测试模式：在 Select 实例的连接上开启事务，finally 中 ROLLBACK 回滚所有 SM2 写入
            System.Data.SQLite.SQLiteTransaction testTx = null;

            IsLearningActive = true;
            try
            {
            WordType WordList = (WordType)wordtype;
            PushWords pushWords = new PushWords();
            if (WordList.WordList != null)
            {
                Recitation(wordtype);
                return;
            }
            Select Query = new Select();
            Query.SelectWordList(); //import database

            // 测试模式：在数据连接上开启事务，finally 中 ROLLBACK
            if (TestMode)
            {
                testTx = Query.DataBase.BeginTransaction();
                ToastBridge.ShowMessage("ToastFish", "🧪 测试模式 — 本轮结束后自动回滚数据");
            }
            Query.GenerateRandomNewCardList(WordList.Number, out List<Card> NewCardLst);
            Query.GetOverdueReviewedCardList(2 * WordList.Number, out List<Card> ReviewedCardLst);
            //NewCardLst.Count;
            //ReviewedCardLst.Count;
            List<Card> LearningCardLst = new List<Card>();
            List<Card> FinishedCardLst = new List<Card>();

            double Score;
            // New Card First
            Debug.WriteLine($"开始背单词 @{DateTime.Now}");
            while (NewCardLst.Count != 0)
            {
                Card newCardi = NewCardLst[0];
                // Cardstatus cardstatus, int numNewCards, int numLearingCards, int numReviewedCards)
                Score = pushWords.pushCard(newCardi, newCardi.status, NewCardLst.Count, LearningCardLst.Count, ReviewedCardLst.Count);
                if (Score == -1)
                {
                    MessageBox.Show("卡题出错！");
                    return;
                }
                NewCardLst.RemoveAt(0);
                newCardi.updateCard(Score);
                if (newCardi.status != Cardstatus.Reviewed)
                {
                    LearningCardLst.Add(newCardi);
                }
                else
                {
                    FinishedCardLst.Add(newCardi);
                }
                LearningCardLst.Sort((a, b) =>
                {
                    // compare a to b to get ascending order
                    int result = a.dateLearingDue.CompareTo(b.dateLearingDue);
                    return result;
                });
                for (int j = 0; j < LearningCardLst.Count; j++)
                {
                    Card Cardj = LearningCardLst[j];
                    if (Cardj.isDue())
                    {
                        Score = pushWords.pushCard(Cardj, Cardj.status, NewCardLst.Count, LearningCardLst.Count, ReviewedCardLst.Count);
                        if (Score == -1)
                        {
                            MessageBox.Show("卡题出错！");
                            return;
                        }
                        Cardj.updateCard(Score);
                        if (Cardj.status == Cardstatus.Reviewed)
                        {
                            LearningCardLst.RemoveAt(j);
                            FinishedCardLst.Add(Cardj);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // === AI 短文后台预生成（2026-07-21） ===
            // 新词阶段结束 → 此时已有足够的目标词 → 启动后台 API 调用，
            // 在用户处理复习词时异步生成。PushMiniReading 阶段检查结果。
            Model.Ai.AiConfig.Load();
            if (Model.Ai.AiConfig.ReadingMode == 1 &&
                (NewCardLst.Count + LearningCardLst.Count + FinishedCardLst.Count) >= 3)
            {
                List<Word> fetchWords = new List<Word>();
                // 收入所有本轮涉及的词（新词 + 学习中 + 复习词），全部参与短文生成
                foreach (var c in FinishedCardLst) { if (fetchWords.Count < 8) fetchWords.Add(c.word); }
                foreach (var c in LearningCardLst) { if (fetchWords.Count < 8) fetchWords.Add(c.word); }
                foreach (var c in NewCardLst)      { if (fetchWords.Count < 8) fetchWords.Add(c.word); }
                foreach (var c in ReviewedCardLst)  { if (fetchWords.Count < 8) fetchWords.Add(c.word); }

                if (fetchWords.Count >= 3)
                {
                    var hws = new List<string>();
                    foreach (var w in fetchWords) hws.Add(w.headWord);
                    var captureWords = fetchWords; // 闭包捕获

                    // 50/50 随机决定题型，只启动一种预生成
                    bool doClozePrefetch = (new Random().Next(2) == 1);
                    _preFetchIsCloze = doClozePrefetch;

                    if (doClozePrefetch)
                    {
                        bool kaoyan = Select.TABLE_NAME.StartsWith("KaoYan");
                        if (kaoyan)
                        {
                            // 考研：20空四选一（考研英语一完形填空真实题型）
                            var cloze4Task = new Task(() =>
                            {
                                try
                                {
                                    Model.Ai.Cloze4Result cr; string cerr;
                                    bool ok = Model.Ai.EssayGenerator.TryGenerateCloze4Choice(hws, out cr, out cerr);
                                    lock (_essayPreFetchLock)
                                    {
                                        _cloze4PreFetchResult = ok ? cr : null;
                                        _cloze4PreFetchWords = ok ? captureWords : null;
                                        if (!ok) _cloze4PreFetchError = cerr;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine("AI 20空四选一预生成异常: " + ex.Message);
                                    lock (_essayPreFetchLock)
                                    {
                                        _cloze4PreFetchResult = null; _cloze4PreFetchWords = null;
                                        _cloze4PreFetchError = ex.Message;
                                    }
                                }
                            });
                            lock (_essayPreFetchLock) { _cloze4PreFetchTask = cloze4Task; }
                            cloze4Task.Start();
                        }
                        else
                        {
                            // 非考研：15选10
                            var clozeTask = new Task(() =>
                            {
                                try
                                {
                                    Model.Ai.ClozeResult cr; string cerr;
                                    bool ok = Model.Ai.EssayGenerator.TryGenerateCloze(hws, Select.TABLE_NAME, out cr, out cerr);
                                    lock (_essayPreFetchLock)
                                    {
                                        _clozePreFetchResult = ok ? cr : null;
                                        _clozePreFetchWords = ok ? captureWords : null;
                                        if (!ok) _clozePreFetchError = cerr;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine("AI 完形填空预生成异常: " + ex.Message);
                                    lock (_essayPreFetchLock)
                                    {
                                        _clozePreFetchResult = null; _clozePreFetchWords = null;
                                        _clozePreFetchError = ex.Message;
                                    }
                                }
                            });
                            lock (_essayPreFetchLock) { _clozePreFetchTask = clozeTask; }
                            clozeTask.Start();
                        }
                    }
                    else
                    {
                        var fetchTask = new Task(() =>
                        {
                            try
                            {
                                Model.Ai.EssayResult result;
                                string error;
                                bool ok = Model.Ai.EssayGenerator.TryGenerate(hws, Select.TABLE_NAME, out result, out error);
                                string diag = ok
                                    ? ("OK en=" + (result.EssayEN != null ? result.EssayEN.Length + "chars" : "null") + " q=" + result.Questions.Count)
                                    : ("FAIL err=" + (error ?? "null") + " diag=" + (Model.Ai.EssayGenerator.LiveDiag ?? "null"));
                                try { System.IO.File.AppendAllText(
                                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Resources", "ai_diag.log"),
                                    DateTime.Now + " GEN " + diag + "\n"); } catch { }
                                lock (_essayPreFetchLock)
                                {
                                    _essayPreFetchResult = new EssayPreFetchResult
                                    {
                                        Success = ok,
                                        Result = ok ? result : null,
                                        Error = ok ? null : error,
                                        EssayWords = captureWords
                                    };
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine("AI 短文预生成异常: " + ex.Message);
                                lock (_essayPreFetchLock)
                                {
                                    _essayPreFetchResult = new EssayPreFetchResult
                                    {
                                        Success = false,
                                        Error = ex.Message,
                                        EssayWords = captureWords
                                    };
                                }
                            }
                        });
                        lock (_essayPreFetchLock) { _essayPreFetchTask = fetchTask; }
                        fetchTask.Start();
                    }
                }
            }

            //Reviewed Card Next
            while (ReviewedCardLst.Count != 0)
            {
                Card newCardi = ReviewedCardLst[0];
                // Cardstatus cardstatus, int numNewCards, int numLearingCards, int numReviewedCards)
                Score = pushWords.pushCard(newCardi, newCardi.status, NewCardLst.Count, LearningCardLst.Count, ReviewedCardLst.Count);
                if (Score == -1)
                {
                    MessageBox.Show("卡题出错！");
                    return;
                }
                ReviewedCardLst.RemoveAt(0);
                newCardi.updateCard(Score);
                if (newCardi.status != Cardstatus.Reviewed)
                {
                    LearningCardLst.Add(newCardi);
                }
                else
                {
                    FinishedCardLst.Add(newCardi);
                }
                LearningCardLst.Sort((a, b) =>
                {
                    // compare a to b to get ascending order
                    int result = a.dateLearingDue.CompareTo(b.dateLearingDue);
                    return result;
                });
                for (int j = 0; j < LearningCardLst.Count; j++)
                {
                    Card Cardj = LearningCardLst[j];
                    if (Cardj.isDue())
                    {
                        Score = pushWords.pushCard(Cardj, Cardj.status, NewCardLst.Count, LearningCardLst.Count, ReviewedCardLst.Count);
                        if (Score == -1)
                        {
                            MessageBox.Show("卡题出错！");
                            return;
                        }
                        Cardj.updateCard(Score);
                        if (Cardj.status == Cardstatus.Reviewed)
                        {
                            LearningCardLst.RemoveAt(j);
                            FinishedCardLst.Add(Cardj);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }

            //the Remain Learing Card
            LearningCardLst.Sort((a, b) =>
            {
                // compare a to b to get ascending order
                int result = a.dateLearingDue.CompareTo(b.dateLearingDue);
                return result;
            });
            while (LearningCardLst.Count != 0)
            {
                Card Cardj = LearningCardLst[0];
                Score = pushWords.pushCard(Cardj, Cardj.status, NewCardLst.Count, LearningCardLst.Count, ReviewedCardLst.Count);
                if (Score == -1)
                {
                    MessageBox.Show("卡题出错！");
                    return;
                }
                Cardj.updateCard(Score);
                if (Cardj.status == Cardstatus.Reviewed)
                {
                    LearningCardLst.RemoveAt(0);
                    FinishedCardLst.Add(Cardj);
                }
                else
                {
                    LearningCardLst.Sort((a, b) =>
                    {
                        // compare a to b to get ascending order
                        int result = a.dateLearingDue.CompareTo(b.dateLearingDue);
                        return result;
                    });
                }

            }

            Debug.WriteLine($"更新数据库 @{DateTime.Now}");
            Query.updateCardDateBase(FinishedCardLst);
            Query.SyncCountTable();  // 同步 Count 表，确保进度计数实时准确
            Debug.WriteLine($"数据库更新完毕 @{DateTime.Now}");

            FinishedCardLst.Sort((b, a) =>
            {
                // compare a to b to get decending order
                int result = a.percentOverdue.CompareTo(b.percentOverdue);
                return result;
            });
            List<Word> RandomList = new List<Word>();
            List<Word> AllFinshedWordList = new List<Word>();

            foreach (var cardi in FinishedCardLst)
            {
                AllFinshedWordList.Add(cardi.word);
                if (cardi.lastScore != Parameters.Easy)
                    RandomList.Add(cardi.word);
            }
            if (RandomList.Count > 0)
            {
                pushWords.PushMessage("背完了！接下来开始测验记忆模糊的单词！");
                pushWords.PushWaitAllQuestions(RandomList, (List<Word>)Query.AllWordList);
            }

            // 学后微阅读（2026-07-17 新增）：例句串读 + 抽查，异常不影响学习收尾
            try { pushWords.PushMiniReading(AllFinshedWordList, (List<Word>)Query.AllWordList, FinishedCardLst); }
            catch (Exception ex) { Debug.WriteLine("微阅读异常: " + ex.Message); }

            pushWords.PushMessage("结束了！恭喜！");

            // 后台刷新学习仪表盘（2026-07-22），不影响收尾流程
            Task.Run(() => RefreshDashboard());

            if (Select.AUTO_LOG != 0)
            {
                CreateLog Log = new CreateLog();
                String LogName = "Log\\" + DateTime.Now.ToString().Replace('/', '-').Replace(' ', '_').Replace(':', '-') + "_英语.xlsx";
                Log.OutputExcel(LogName, AllFinshedWordList, "英语");
            }

            }
            finally
            {
                if (testTx != null)
                {
                    try { testTx.Rollback(); }
                    catch (Exception ex) { Debug.WriteLine("TestTx Rollback failed: " + ex.Message); }
                    try { testTx.Dispose(); } catch { }
                    ToastBridge.ShowMessage("ToastFish", "✅ 测试模式 — 数据已回滚，本轮学习未保存");
                }
                IsLearningActive = false;
            }
        }

        /// <summary>
        /// 背诵单词
        /// </summary>
        public static void Recitation(Object Words)
        {
            IsLearningActive = true;
            try
            {
            Select Query = new Select();
            PushWords pushWords = new PushWords();

            WordType WordList = (WordType)Words;
            List<Word> RandomList;
            bool ImportFlag = true;

            if (WordList.WordList == null)
            {
                Query.SelectWordList();
                RandomList = Query.GetRandomWordList((int)WordList.Number);
                ImportFlag = false;
            }
            else
            {
                RandomList = WordList.WordList;
            }

            if (ImportFlag == false)
            {
                CreateLog Log = new CreateLog();
                String LogName = "Log\\" + DateTime.Now.ToString().Replace('/', '-').Replace(' ', '_').Replace(':', '-') + "_英语.xlsx";
                Log.OutputExcel(LogName, RandomList, "英语");
            }

            if (RandomList.Count == 0 && ImportFlag == false)
            {
                pushWords.PushMessage("好..好像词库里没有单词了，您就是摸鱼之王！");
                return;
            }
            else if (RandomList.Count == 0 && ImportFlag == true)
            {
                return;
            }
            List<Word> CopyList = pushWords.Clone<Word>(RandomList);
            Word CurrentWord = new Word();
            Debug.WriteLine($"开始背单词 @{DateTime.Now}");
            while (CopyList.Count != 0)
            {
                if (pushWords.WORD_CURRENT_STATUS != 3)
                    CurrentWord = CopyList[0];// GetRandomWord(CopyList);
                pushWords.PushOneWord(CurrentWord);

                pushWords.WORD_CURRENT_STATUS = 2;
                while (pushWords.WORD_CURRENT_STATUS == 2)
                {
                    int result = -1;
                    try
                    {
                        var task = pushWords.ProcessToastNotificationRecitation();
                        result = task.Result;
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.Message);
                        return;
                    }
                    if (result == 0)
                    {
                        pushWords.WORD_CURRENT_STATUS = 1;
                    }
                    else if (result == 1)
                    {
                        pushWords.WORD_CURRENT_STATUS = 0;
                    }
                    else if (result == 2)
                    {
                        pushWords.WORD_CURRENT_STATUS = 3;
                        string word_pron, word_save_name;
                        switch (Select.ENG_TYPE)
                        {
                            case 1:
                                word_save_name = CurrentWord.headWord + "_us";
                                word_pron = CurrentWord.headWord + "&type=1";
                                break;
                            default:
                                word_save_name = CurrentWord.headWord + "_uk";
                                word_pron = CurrentWord.headWord + "&type=2";
                                break;
                        }
                        List<string> words = new List<string>();
                        //将Person对象放入集合
                        words.Add(word_save_name);
                        words.Add(word_pron);
                        bool ret = Download.DownloadMp3.PlayMp3(words);
                        if (ret == false)
                        {
                            SpeechSynthesizer synth = new SpeechSynthesizer();
                            synth.SpeakAsync(CurrentWord.headWord);
                        }
                    }
                }
                if (pushWords.WORD_CURRENT_STATUS == 1)
                {
                    if (ImportFlag == false)
                    {
                        Query.UpdateWord(CurrentWord.wordRank);
                        Query.UpdateCount();
                    }
                    CopyList.Remove(CurrentWord);
                }
                else if (pushWords.WORD_CURRENT_STATUS == 0)
                {
                    if (CopyList.Count == 2)
                    {
                        CopyList.Remove(CurrentWord);
                        CopyList.Add(CurrentWord);
                    }
                    else if (CopyList.Count >= 3)
                    {
                        CopyList.Remove(CurrentWord);
                        Random Rd = new Random();
                        int Index = Rd.Next(CopyList.Count - 1);
                        CopyList.Insert(Index + 1, CurrentWord);
                    }

                }
            }
            Debug.WriteLine($"背完了！接下来开始测验！@{DateTime.Now}");
            pushWords.PushMessage("背完了！接下来开始测验！");
            Thread.Sleep(3000);

            /* 背诵结束 */
            Debug.WriteLine($"开始做题 @{DateTime.Now}");
            Query.SelectWordList();
            pushWords.PushWaitAllQuestions(RandomList, (List<Word>)Query.AllWordList);

            Debug.WriteLine($"结束了！恭喜！ @{DateTime.Now}");

            // // History.Clear()
            pushWords.PushMessage("结束了！恭喜！");
            }
            finally { IsLearningActive = false; }
        }

        public void UnorderWord(Object Num)
        {
            IsLearningActive = true;
            try
            {
            int Number = (int)Num;
            Select Query = new Select();
            Query.SelectWordList();
            List<Word> TestList = Query.GetRandomWords(Number);

            CreateLog Log = new CreateLog();
            String LogName = "Log\\" + DateTime.Now.ToString().Replace('/', '-').Replace(' ', '_').Replace(':', '-') + "_随机英语单词.xlsx";
            Log.OutputExcel(LogName, TestList, "英语");

            Word CurrentWord = new Word();

            while (TestList.Count != 0)
            {
                // // History.Clear()
                Thread.Sleep(500);
                CurrentWord = GetRandomWord(TestList);
                List<Word> FakeWordList = Query.GetRandomWords(2);

                PushOneTransQuestion(CurrentWord, FakeWordList[0].headWord, FakeWordList[1].headWord);

                QUESTION_CURRENT_STATUS = 2;
                while (QUESTION_CURRENT_STATUS == 2)
                {
                    var task = ProcessToastNotificationQuestion();
                    if (task.Result == 1)
                        QUESTION_CURRENT_STATUS = 1;
                    else if (task.Result == 0)
                        QUESTION_CURRENT_STATUS = 0;
                    else if (task.Result == -1)
                        QUESTION_CURRENT_STATUS = -1;
                }

                if (QUESTION_CURRENT_STATUS == 1)
                {
                    TestList.Remove(CurrentWord);
                    Thread.Sleep(500);
                }
                else if (QUESTION_CURRENT_STATUS == 0)
                {
                    //CopyList.Remove(CurrentWord);
                    ToastBridge.ShowMessage("ToastFish", "错误 正确答案：" + AnswerDict[QUESTION_CURRENT_RIGHT_ANSWER.ToString()] + '.' + CurrentWord.headWord, 3000);
                    Thread.Sleep(3000);
                }
            }
            // // History.Clear()
            PushMessage("结束了！恭喜！");
            }
            finally { IsLearningActive = false; }
        }

        /// <summary>
        /// 推送一条通知
        /// </summary>
        public void PushMessage(string Message, string Buttom = "")
        {
            ToastBridge.ShowMessage("ToastFish", Message);
        }

        /// <summary>
        /// 推送一个单词
        /// </summary>
        /// <param name="CurrentWord"></param>
        public void PushOneWord(Word CurrentWord)
        {
            // // History.Clear()
            string Phoneme;
            switch (Select.ENG_TYPE)
            {
                case 1:
                    Phoneme = CurrentWord.usPhone;
                    break;
                default:
                    Phoneme = CurrentWord.ukPhone;
                    break;
            }
            string WordPhonePosTran = CurrentWord.headWord + "  (" + Phoneme + ")\n" + CurrentWord.pos + ". " + CurrentWord.tranCN;
            string SentenceTran = "";
            if (CurrentWord.sentence != null && CurrentWord.sentence.Length < 50)
            {
                SentenceTran = CurrentWord.sentence + '\n' + CurrentWord.sentenceCN;
            }
            else if (CurrentWord.phrase != null)
            {
                SentenceTran = CurrentWord.phrase + '\n' + CurrentWord.phraseCN;
            }
            ToastBridge.ShowMessage("ToastFish", WordPhonePosTran + "\n" + SentenceTran, 5000);
        }

        public void PushOneWordSM2(Word CurrentWord, Cardstatus cardstatus, int numNewCards, int numLearingCards, int numReviewedCards)
        {
            // // History.Clear()
            string Phoneme;
            switch (Select.ENG_TYPE)
            {
                case 1:
                    Phoneme = CurrentWord.usPhone;
                    break;
                default:
                    Phoneme = CurrentWord.ukPhone;
                    break;
            }
            string word = CurrentWord.headWord;
            string phoneme = string.IsNullOrEmpty(Phoneme) ? "" : "  /" + Phoneme + "/";
            string posTran = string.IsNullOrEmpty(CurrentWord.pos) ? CurrentWord.tranCN : CurrentWord.pos + " " + CurrentWord.tranCN;
            string sentence = (CurrentWord.sentence != null && CurrentWord.sentence.Length < 50) ? CurrentWord.sentence : "";
            string sentenceCN = (CurrentWord.sentence != null && CurrentWord.sentence.Length < 50) ? CurrentWord.sentenceCN : "";
            string phrase = (CurrentWord.phrase != null) ? CurrentWord.phrase : "";
            string phraseCN = (CurrentWord.phraseCN != null) ? CurrentWord.phraseCN : "";

            // 用 ‖ 分隔各段落，弹窗内分别渲染不同样式
            string displayText = word + "‖" + phoneme + "‖" + posTran + "‖" + sentence + "‖" + sentenceCN + "‖" + phrase + "‖" + phraseCN;

            string HeadTile;
            if (cardstatus == Cardstatus.Reviewed)
                HeadTile = "复习中  |  新词:" + numNewCards + "  学习中:" + numLearingCards + "  待复习:" + numReviewedCards;
            else if (cardstatus == Cardstatus.New)
                HeadTile = "新单词  |  新词:" + numNewCards + "  学习中:" + numLearingCards + "  待复习:" + numReviewedCards;
            else if (cardstatus == Cardstatus.Step1 || cardstatus == Cardstatus.Step2)
                HeadTile = "新学·阶段" + (int)cardstatus + "  |  新词:" + numNewCards + "  学习中:" + numLearingCards + "  待复习:" + numReviewedCards;
            else
                HeadTile = "重学·阶段" + ((int)cardstatus - (int)Cardstatus.Step2) + "  |  新词:" + numNewCards + "  学习中:" + numLearingCards + "  待复习:" + numReviewedCards;

            // 非阻塞显示弹窗
            NotificationForm.ShowWordPopup(
                displayText + "‖‖" + HeadTile,
                new[] { "没印象", "模糊", "记住", "牢记" },
                answerIdx =>
                {
                    if (answerIdx >= 0 && answerIdx <= 3)
                        HotKeytObservable.raiseEvent((answerIdx + 1).ToString());
                },
                NotificationForm.TIMEOUT_WORD);
        }

        /// <summary>
        /// 推送翻译和填空选择题/
        /// </summary>
        public void PushWaitAllQuestions(List<Word> RandomList, List<Word> AllWordList)
        {
            /* 背诵结束 */
            //中译英
            List<Word> CopyList = Clone<Word>(RandomList);
            Word CurrentWord;
            CopyList.RemoveAll(word =>
            {
                bool result = false;
                if (word.question != null && word.question != "")
                    result = true;
                return result;
            });
            Debug.WriteLine($"开始翻译选择 @{DateTime.Now}");
            while (CopyList.Count != 0)
            {
                // // History.Clear()
                Thread.Sleep(500);
                CurrentWord = CopyList[0];
                List<Word> rndWords;
                if (RandomList.Count >= 10)
                {
                    rndWords = GetRandomWordLst(CurrentWord, RandomList, 2);
                }
                {
                    rndWords = GetRandomWordLst(CurrentWord, AllWordList, 2);
                }

                bool result = PushWaitTransQuestion(CurrentWord, rndWords[0].headWord, rndWords[1].headWord);
                if (result)
                {
                    CopyList.RemoveAt(0);
                    Thread.Sleep(500);
                }
                else
                {
                    //CopyList.Remove(CurrentWord);
                    ToastBridge.ShowMessage("ToastFish", "错误 正确答案：" + AnswerDict[QUESTION_CURRENT_RIGHT_ANSWER.ToString()] + '.' + CurrentWord.headWord, 3000);
                    CopyList.RemoveAt(0);
                    CopyList.Add(CurrentWord);
                    Thread.Sleep(5000);
                }
            }
            //填空题
            CopyList = Clone<Word>(RandomList);
            CopyList.RemoveAll(word =>
            {
                bool result = false;
                if (word.question == null || word.question == "")
                    result = true;
                return result;
            });
            Debug.WriteLine($"开始填空 @{DateTime.Now}");
            while (CopyList.Count != 0)
            {
                // // History.Clear()
                //CurrentWord = GetRandomWord(CopyList);
                CurrentWord = CopyList[0];
                QUESTION_CURRENT_RIGHT_ANSWER = int.Parse(CurrentWord.rightIndex) - 1;
                PushOneQuestion(CurrentWord);
                bool isFinished = PushWaitFillQuestion(CurrentWord);

                if (isFinished)
                {
                    CopyList.RemoveAt(0);
                    // CopyList.Remove(CurrentWord);
                    //Thread.Sleep(500);
                }
                else
                {
                    //RandomList.Remove(CurrentWord);
                    string explainText = "错误, 正确答案：" + AnswerDict[QUESTION_CURRENT_RIGHT_ANSWER.ToString()];
                    if (!string.IsNullOrEmpty(CurrentWord.explain))
                        explainText += "\n" + CurrentWord.explain;
                    ToastBridge.ShowMessage("ToastFish", explainText, 5000);
                    Thread.Sleep(6000);
                    CopyList.RemoveAt(0);
                    CopyList.Add(CurrentWord);
                }
            }
            // // History.Clear()

        }

        /// <summary>
        /// 推送一道选择题
        /// </summary>
        public bool PushWaitFillQuestion(Word CurrentWord)
        {
            bool isFinished = false;
            int rst = -1;
            PushOneQuestion(CurrentWord);
            try
            {
                var task = ProcessToastNotificationQuestion();
                rst = task.Result;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
            if (rst == 1)
                isFinished = true;
            return isFinished;
        }

        public void PushOneQuestion(Word CurrentWord)
        {
            string Question = CurrentWord.question;
            string A = CurrentWord.choiceIndexOne;
            string B = CurrentWord.choiceIndexTwo;
            string C = CurrentWord.choiceIndexThree;
            string D = CurrentWord.choiceIndexFour;

            NotificationForm.ShowQuizPopup("选择题", Question,
                new[] { A, B, C, D },
                answerIdx =>
                {
                    if (answerIdx >= 0 && answerIdx <= 3)
                        HotKeytObservable.raiseEvent((answerIdx + 1).ToString());
                });
        }

        /// <summary>
        /// 推送翻译问题
        /// </summary>
        public bool PushWaitTransQuestion(Word CurrentWord, string headWord1, string headWord2)
        {
            bool isFinshed = false;
            int rst = -1;
            PushOneTransQuestion(CurrentWord, headWord1, headWord2);

            try
            {
                var task = ProcessToastNotificationQuestion();
                rst = task.Result;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
            if (rst == 1)
                isFinshed = true;
            return isFinshed;
        }

        public void PushOneTransQuestion(Word CurrentWord, string B, string C)
        {
            string Question = CurrentWord.tranCN;
            string A = CurrentWord.headWord;

            Random Rd = new Random();
            int AnswerIndex = Rd.Next(3);
            QUESTION_CURRENT_RIGHT_ANSWER = AnswerIndex;

            // 按正确顺序排列选项 [A, B, C]，正确答案在 AnswerIndex 位置
            string[] ordered;
            if (AnswerIndex == 0)
                ordered = new[] { "A." + A, "B." + B, "C." + C };
            else if (AnswerIndex == 1)
                ordered = new[] { "A." + B, "B." + A, "C." + C };
            else
                ordered = new[] { "A." + C, "B." + B, "C." + A };

            NotificationForm.ShowQuizPopup("翻译", Question, ordered,
                answerIdx =>
                {
                    if (answerIdx >= 0 && answerIdx <= 2)
                        HotKeytObservable.raiseEvent((answerIdx + 1).ToString());
                });
        }

        /// <summary>
        /// 等待微阅读弹窗的用户操作："1"=开始测验, "RT"=超时跳过
        /// </summary>
        public async Task<int> ProcessToastNotificationReading()
        {
            var Tcs = new TaskCompletionSource<int>();

            using (HotKeytObservable.Subscribe(events =>
            {
                switch (events)
                {
                    case "1": Tcs.TrySetResult(1); break;
                    case "RT": Tcs.TrySetResult(0); break;
                }
            }))
            {
                return await Tcs.Task;
            }
        }

        /// <summary>
        /// AI 生成超过 180 秒时询问用户是否继续等待（2026-09-01 新增）。
        /// 返回 true=继续等待，false=立即终止。弹窗超时/异常默认「继续等待」，不打断学习。
        /// </summary>
        private static bool AskContinueOrAbort()
        {
            try
            {
                Task<int> t = ToastBridge.ShowInteractive(
                    "AI 生成时间较长",
                    "已超过 3 分钟仍未完成，是否继续等待？",
                    new[] { "继续等待", "立即终止" }, 30000);
                if (!t.Wait(35000)) return true;   // 弹窗未正常返回：兜底继续等待
                return t.Result != 1;               // 0=继续、-1=超时未点 → 继续；1=立即终止
            }
            catch { return true; }
        }

        /// <summary>
        /// 学后微阅读（2026-07-17 新增 / 2026-07-21 重构）：
        /// AI 短文支持后台预生成 + 阅读前信息页 + 读后 SM2+ 自适应反馈。
        /// finishedCards 用于获取本轮最新的 difficulty 值（而非 Word 对象中可能过期的值）。
        /// </summary>
        public void PushMiniReading(List<Word> roundWords, List<Word> AllWordList, List<Card> finishedCards)
        {
            // === 模式 1：AI 后阅读（预生成版，2026-07-22） ===
            Model.Ai.AiConfig.Load();
            if (Model.Ai.AiConfig.ReadingMode == 1 && roundWords.Count >= 3)
            {
                // 读取预生成阶段决定的题型标志
                bool useCloze = _preFetchIsCloze;
                _preFetchIsCloze = false; // 复位

                if (useCloze)
                {
                    bool _kaoyan = Select.TABLE_NAME.StartsWith("KaoYan");
                    if (_kaoyan)
                    {
                        // === 20空四选一 完形填空（考研英语一真实题型）===
                        Model.Ai.Cloze4Result cloze4 = null;
                        List<Word> cloze4Words = null;
                        Task cloze4Task = null;
                        lock (_essayPreFetchLock)
                        {
                            cloze4Task = _cloze4PreFetchTask;
                            _cloze4PreFetchTask = null;
                            if (cloze4Task == null)
                            {
                                cloze4 = _cloze4PreFetchResult;
                                cloze4Words = _cloze4PreFetchWords;
                                _cloze4PreFetchResult = null;
                                _cloze4PreFetchWords = null;
                            }
                        }
                        // Wait OUTSIDE lock
                        if (cloze4Task != null)
                        {
                            if (!cloze4Task.IsCompleted)
                            {
                                PushMessage("AI 正在生成 20空四选一 完形填空...");
                                int elapsed = 0;
                                bool asked = false;
                                const int ABORT_TIMEOUT_MS = 180000;
                                while (!cloze4Task.IsCompleted)
                                {
                                    if (cloze4Task.Wait(5000)) break;
                                    elapsed += 5000;
                                    string t = Model.Ai.EssayGenerator.LiveThinking;
                                    if (!string.IsNullOrEmpty(t))
                                    {
                                        string s = t.Replace('\n', ' ').Replace('\r', ' ');
                                        if (s.Length > 80) s = "…" + s.Substring(s.Length - 80);
                                        PushMessage("AI 思考: " + s);
                                    }
                                    if (!asked && elapsed >= ABORT_TIMEOUT_MS)
                                    {
                                        asked = true;
                                        if (AskContinueOrAbort())
                                        {
                                            PushMessage("已继续等待 AI 生成...");
                                            continue;
                                        }
                                        Model.Ai.EssayGenerator.AbortActiveRequest();
                                        string reason = "生成超时（>180 秒），用户终止";
                                        string diag = Model.Ai.EssayGenerator.LiveDiag;
                                        string thinking = Model.Ai.EssayGenerator.LiveThinking;
                                        if (!string.IsNullOrEmpty(thinking))
                                        {
                                            string s = thinking.Replace('\n', ' ').Replace('\r', ' ');
                                            if (s.Length > 80) s = "…" + s.Substring(s.Length - 80);
                                            reason += " 思考:" + s;
                                        }
                                        if (!string.IsNullOrEmpty(diag)) reason += " " + diag;
                                        bool cloze4Done;
                                        lock (_essayPreFetchLock)
                                        {
                                            cloze4Done = (_cloze4PreFetchResult != null);
                                            if (!cloze4Done) _cloze4PreFetchError = reason;
                                        }
                                        try { System.IO.File.AppendAllText(
                                            System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Resources", "ai_diag.log"),
                                            DateTime.Now + "\nCLOZE4 " + reason + "\n"); } catch { }
                                        if (!cloze4Done) PushMessage("已停止本次 AI 生成");
                                        break;
                                    }
                                }
                            }
                            lock (_essayPreFetchLock)
                            {
                                cloze4 = _cloze4PreFetchResult;
                                cloze4Words = _cloze4PreFetchWords;
                                _cloze4PreFetchResult = null;
                                _cloze4PreFetchWords = null;
                            }
                        }

                        if (cloze4 == null)
                        {
                            string err = _cloze4PreFetchError ?? Model.Ai.EssayGenerator.LiveError;
                            bool timeoutNotified = !string.IsNullOrEmpty(_cloze4PreFetchError) && _cloze4PreFetchError.Contains("超时");
                            string diag = Model.Ai.EssayGenerator.LiveDiag;
                            string msg = "AI 生成失败";
                            if (!string.IsNullOrEmpty(err)) msg += ": " + err;
                            if (!string.IsNullOrEmpty(diag)) msg += " [" + diag + "]";
                            try { System.IO.File.WriteAllText(
                                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Resources", "ai_diag.log"),
                                DateTime.Now + "\nCLOZE4 " + msg + "\n"); } catch { }
                            if (!timeoutNotified) { PushMessage(msg); Thread.Sleep(6000); }
                        }
                        _cloze4PreFetchError = null;

                        if (cloze4 != null)
                        {
                            using (var done = new ManualResetEvent(false))
                            {
                                int cloze4Result = 0;
                                NotificationForm.ShowCloze4ChoicePopup(cloze4,
                                    cloze4Words.Select(w => w.headWord).ToList(),
                                    r => { cloze4Result = r; done.Set(); });
                                done.WaitOne();
                                int attempted = (cloze4Result >> 16) & 0xFFFF;
                                int correct = cloze4Result & 0xFFFF;
                                if (attempted > 0)
                                {
                                    ApplyEssaySM2Feedback(cloze4Words, finishedCards, attempted, correct);
                                    SaveCloze4Log(cloze4Words, cloze4, attempted, correct);
                                }
                            }
                            return;
                        }
                        // 20空四选一失败 → fall through 到例句串读
                    }
                    if (!_kaoyan)
                    {
                    // === 15选10 完形填空（非考研）===
                    Model.Ai.ClozeResult cloze = null;
                    List<Word> clozeWords = null;
                    Task clozeTask = null;
                    lock (_essayPreFetchLock)
                    {
                        clozeTask = _clozePreFetchTask;
                        _clozePreFetchTask = null;
                        if (clozeTask == null)
                        {
                            cloze = _clozePreFetchResult;
                            clozeWords = _clozePreFetchWords;
                            _clozePreFetchResult = null;
                            _clozePreFetchWords = null;
                        }
                    }
                    // Wait OUTSIDE lock — Task 完成后需要同一把锁写结果，锁内 Wait 导致死锁
                    if (clozeTask != null)
                    {
                        if (!clozeTask.IsCompleted)
                        {
                            PushMessage("AI 正在生成 15 选 10 完形填空...");
                            int elapsed = 0;
                            bool asked = false;                    // 已询问过用户则不再重复询问
                            const int ABORT_TIMEOUT_MS = 180000;   // 生成"不限时"，180 秒后询问是否继续
                            while (!clozeTask.IsCompleted)
                            {
                                if (clozeTask.Wait(5000)) break;
                                elapsed += 5000;
                                string t = Model.Ai.EssayGenerator.LiveThinking;
                                if (!string.IsNullOrEmpty(t))
                                {
                                    string s = t.Replace('\n', ' ').Replace('\r', ' ');
                                    if (s.Length > 80) s = "…" + s.Substring(s.Length - 80);
                                    PushMessage("AI 思考: " + s);
                                }
                                if (!asked && elapsed >= ABORT_TIMEOUT_MS)
                                {
                                    asked = true;
                                    if (AskContinueOrAbort())
                                    {
                                        // 用户选择继续等待（或超时未点）：不再询问，等 HTTP 层 600s 超时/降级兜底
                                        PushMessage("已继续等待 AI 生成...");
                                        continue;
                                    }
                                    // 用户选择立即终止：主动中止 HTTP + 记录失败原因
                                    Model.Ai.EssayGenerator.AbortActiveRequest();
                                    string reason = "生成超时（>180 秒），用户终止";
                                    string diag = Model.Ai.EssayGenerator.LiveDiag;
                                    string thinking = Model.Ai.EssayGenerator.LiveThinking;
                                    if (!string.IsNullOrEmpty(thinking))
                                    {
                                        string s = thinking.Replace('\n', ' ').Replace('\r', ' ');
                                        if (s.Length > 80) s = "…" + s.Substring(s.Length - 80);
                                        reason += " 思考:" + s;
                                    }
                                    if (!string.IsNullOrEmpty(diag)) reason += " " + diag;
                                    // 已生成则保留结果、不弹「已停止」；仅未就绪时标记终止（2026-09-01 与 ESSAY 统一）
                                    bool clozeDone;
                                    lock (_essayPreFetchLock)
                                    {
                                        clozeDone = (_clozePreFetchResult != null);
                                        if (!clozeDone) _clozePreFetchError = reason;
                                    }
                                    try { System.IO.File.AppendAllText(
                                        System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Resources", "ai_diag.log"),
                                        DateTime.Now + "\nCLOZE " + reason + "\n"); } catch { }
                                    if (!clozeDone) PushMessage("已停止本次 AI 生成");
                                    break;
                                }
                            }
                        }
                        // Re-enter lock to read result
                        lock (_essayPreFetchLock)
                        {
                            cloze = _clozePreFetchResult;
                            clozeWords = _clozePreFetchWords;
                            _clozePreFetchResult = null;
                            _clozePreFetchWords = null;
                        }
                    }

                    // Task 完成但结果为 null → 展示错误，sleep 防覆盖
                    if (cloze == null)
                    {
                        string err = _clozePreFetchError ?? Model.Ai.EssayGenerator.LiveError;
                        bool timeoutNotified = !string.IsNullOrEmpty(_clozePreFetchError) && _clozePreFetchError.Contains("超时");
                        string diag = Model.Ai.EssayGenerator.LiveDiag;
                        string msg = "AI 生成失败";
                        if (!string.IsNullOrEmpty(err)) msg += ": " + err;
                        if (!string.IsNullOrEmpty(diag)) msg += " [" + diag + "]";
                        try { System.IO.File.WriteAllText(
                            System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Resources", "ai_diag.log"),
                            DateTime.Now + "\nCLOZE " + msg + "\n"); } catch { }
                        if (!timeoutNotified) { PushMessage(msg); Thread.Sleep(6000); }
                    }
                    _clozePreFetchError = null;

                    if (cloze != null)
                    {
                        using (var done = new ManualResetEvent(false))
                        {
                            int clozeResult = 0;
                            NotificationForm.ShowClozePopup(cloze,
                                clozeWords.Select(w => w.headWord).ToList(),
                                r => { clozeResult = r; done.Set(); });
                            done.WaitOne();
                            int attempted = (clozeResult >> 16) & 0xFFFF;
                            int correct = clozeResult & 0xFFFF;

                            if (attempted > 0)
                            {
                                ApplyEssaySM2Feedback(clozeWords, finishedCards, attempted, correct);
                                SaveClozeLog(clozeWords, cloze, attempted, correct);
                            }
                        }
                        return;
                    }
                    // 完形填空失败 → fall through 到例句串读
                    }
                }
                else
                {
                // === AI 短文选择题 ===
                EssayPreFetchResult fetched = null;
                Task essayTask = null;
                lock (_essayPreFetchLock)
                {
                    essayTask = _essayPreFetchTask;
                    _essayPreFetchTask = null;
                    if (essayTask == null)
                    {
                        fetched = _essayPreFetchResult;
                        _essayPreFetchResult = null;
                    }
                }
                // Wait OUTSIDE lock — 防止死锁
                if (essayTask != null)
                {
                    if (!essayTask.IsCompleted)
                    {
                        PushMessage("AI 正在生成短文...");
                        int elapsed = 0;
                        bool asked = false;                    // 已询问过用户则不再重复询问
                        const int ABORT_TIMEOUT_MS = 180000;   // 生成"不限时"，180 秒后询问是否继续
                        while (!essayTask.IsCompleted)
                        {
                            if (essayTask.Wait(5000)) break;
                            elapsed += 5000;
                            string t = Model.Ai.EssayGenerator.LiveThinking;
                            if (!string.IsNullOrEmpty(t))
                            {
                                string s = t.Replace('\n', ' ').Replace('\r', ' ');
                                if (s.Length > 80) s = "…" + s.Substring(s.Length - 80);
                                PushMessage("AI 思考: " + s);
                            }
                            if (!asked && elapsed >= ABORT_TIMEOUT_MS)
                            {
                                asked = true;
                                if (AskContinueOrAbort())
                                {
                                    // 用户选择继续等待（或超时未点）：不再询问，等 HTTP 层 600s 超时/降级兜底
                                    PushMessage("已继续等待 AI 生成...");
                                    continue;
                                }
                                // 用户选择立即终止：主动中止 HTTP + 记录失败原因
                                Model.Ai.EssayGenerator.AbortActiveRequest();
                                string reason = "生成超时（>180 秒），用户终止";
                                string diag = Model.Ai.EssayGenerator.LiveDiag;
                                string thinking = Model.Ai.EssayGenerator.LiveThinking;
                                if (!string.IsNullOrEmpty(thinking))
                                {
                                    string s = thinking.Replace('\n', ' ').Replace('\r', ' ');
                                    if (s.Length > 80) s = "…" + s.Substring(s.Length - 80);
                                    reason += " 思考:" + s;
                                }
                                if (!string.IsNullOrEmpty(diag)) reason += " " + diag;
                                // 已生成则保留结果、不弹「已停止」；仅未就绪时标记终止（2026-09-01 与 CLOZE 统一）
                                bool essayDone;
                                lock (_essayPreFetchLock)
                                {
                                    essayDone = (_essayPreFetchResult != null);
                                    if (!essayDone)
                                        _essayPreFetchResult = new EssayPreFetchResult { Success = false, Result = null, Error = reason, EssayWords = null };
                                }
                                try { System.IO.File.AppendAllText(
                                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Resources", "ai_diag.log"),
                                    DateTime.Now + "\n" + reason + "\n"); } catch { }
                                if (!essayDone) PushMessage("已停止本次 AI 生成");
                                break;
                            }
                        }
                    }
                    lock (_essayPreFetchLock)
                    {
                        fetched = _essayPreFetchResult;
                        _essayPreFetchResult = null;
                    }
                }

                // 预取超时/失败 → 展示错误，sleep 防止被后续消息覆盖
                if (fetched == null || !fetched.Success)
                {
                    string err = (fetched != null) ? fetched.Error : Model.Ai.EssayGenerator.LiveError;
                    bool timeoutNotified = !string.IsNullOrEmpty(err) && err.Contains("超时");
                    string diag = Model.Ai.EssayGenerator.LiveDiag;
                    string msg = "AI 生成失败";
                    if (!string.IsNullOrEmpty(err)) msg += ": " + err;
                    if (!string.IsNullOrEmpty(diag)) msg += " [" + diag + "]";
                    // 写诊断文件供事后查看
                    try { System.IO.File.WriteAllText(
                        System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Resources", "ai_diag.log"),
                        DateTime.Now + "\n" + msg + "\nfetched=" + (fetched != null) + "\n"); } catch { }
                    if (!timeoutNotified) { PushMessage(msg); Thread.Sleep(6000); }
                }

                // 3) 成功 → 阻塞展示短文+内嵌测验
                if (fetched != null && fetched.Success)
                {
                    var qs = fetched.Result.Questions.Count > 0 ? fetched.Result.Questions : null;
                    using (var done = new ManualResetEvent(false))
                    {
                        int quizResult = 0;
                        NotificationForm.ShowEssayPopup(
                            fetched.Result.EssayEN, fetched.Result.EssayCN,
                            fetched.EssayWords.Select(w => w.headWord).ToList(),
                            fetched.EssayWords, qs,
                            result => { quizResult = result; done.Set(); });
                        done.WaitOne(); // 阻塞直到用户完成阅读+测验
                        int attempted = (quizResult >> 16) & 0xFFFF;
                        int correct = quizResult & 0xFFFF;

                        // 持久化短文记录（2026-07-22）
                        SaveEssayLog(fetched, attempted, correct);

                        if (attempted > 0)
                            ApplyEssaySM2Feedback(fetched.EssayWords, finishedCards, attempted, correct);
                    }
                    return; // AI 短文完成，不再走例句路径
                }
                } // end else (essay path)
            }
            // AI 短文失败/未启用 → fall through 到例句路径（下行）

            // === 例句串读（默认模式 / AI 失败的回退路径） ===
            {
                // 筛选有效例句
                List<Word> candidates = new List<Word>();
                foreach (var w in roundWords)
                    if (!string.IsNullOrEmpty(w.sentence) && w.sentence.Trim().Length >= 15)
                        candidates.Add(w);
                if (candidates.Count < 3) return;
                List<Word> reading = candidates.GetRange(0, Math.Min(6, candidates.Count));

                List<string[]> items = new List<string[]>();
                foreach (var w in reading)
                    items.Add(new[] { w.headWord, w.sentence.Trim(), (w.sentenceCN ?? "").Trim() });

                PushMessage("最后来读一读，在句子里认出它们！");
                Thread.Sleep(3000);

                NotificationForm.ShowReadingPopup(items, idx =>
                {
                    HotKeytObservable.raiseEvent(idx == 0 ? "1" : "RT");
                });

                // 等待用户操作
                int rst = -1;
                try { rst = ProcessToastNotificationReading().Result; }
                catch (Exception e) { Debug.WriteLine(e.Message); }
                if (rst != 1) return;

                // 例句测验：抽 2 词做"看释义选词"
                Random rd = new Random();
                List<Word> pool = new List<Word>(reading);
                int quizCount = Math.Min(2, pool.Count);
                for (int i = 0; i < quizCount; i++)
                {
                    Word cur = pool[rd.Next(pool.Count)]; pool.Remove(cur);
                    List<Word> fakes = GetRandomWordLst(cur, AllWordList, 2);
                    bool ok = PushWaitTransQuestion(cur, fakes[0].headWord, fakes[1].headWord);
                    if (ok) { Thread.Sleep(500); }
                    else
                    {
                        ToastBridge.ShowMessage("ToastFish", "错误 正确答案：" +
                            AnswerDict[QUESTION_CURRENT_RIGHT_ANSWER.ToString()] + '.' + cur.headWord, 3000);
                        Thread.Sleep(3500);
                    }
                }
            }
        }

        /// <summary>AI 短文读后 SM2+ 自适应反馈（Sigmoid 非线性，2026-07-21）。</summary>
        private static void ApplyEssaySM2Feedback(List<Word> essayWords, List<Card> finishedCards,
            int attempted, int correct)
        {
            if (TestMode) return; // 测试模式：事务会回滚，跳过 SM2 反馈避免多连接写 WAL 冲突
            if (essayWords == null || essayWords.Count == 0) return;

            var cardMap = new Dictionary<int, Card>();
            if (finishedCards != null)
            {
                foreach (var c in finishedCards)
                    if (!cardMap.ContainsKey(c.word.wordRank))
                        cardMap[c.word.wordRank] = c;
            }

            double correctRate = (double)correct / Math.Max(1, attempted);
            List<Card> updatedCards = new List<Card>();
            bool isGood = correctRate >= 0.5;

            foreach (var w in essayWords)
            {
                double diff = cardMap.ContainsKey(w.wordRank)
                    ? Math.Max(0.01, Math.Min(0.99, cardMap[w.wordRank].difficulty))
                    : Math.Max(0.01, Math.Min(0.99, w.difficulty));
                double s = Sigmoid((diff - 0.25) * 10.0);
                double score = isGood
                    ? Math.Max(0.4, Math.Min(1.0, 0.77 + s * 0.18))
                    : Math.Max(0.4, Math.Min(1.0, 0.73 - s * 0.18));
                Card c2 = new Card(w); c2.updateCard(score); updatedCards.Add(c2);
            }

            Select updateSelect = new Select();
            updateSelect.SelectWordList();
            updateSelect.updateCardDateBase(updatedCards);

            if (isGood)
                ToastBridge.ShowMessage("ToastFish", "阅读完成 ✓  本轮 " + essayWords.Count + " 个词的掌握度已自动提升", 4000);
            else
                ToastBridge.ShowMessage("ToastFish", "阅读完成 — 没关系，这轮较难，单词复习节奏已自动调整", 4000);
            Thread.Sleep(1500);
        }

        /// <summary>
        /// 克隆Word列表
        /// </summary>
        /// <typeparam name="Word"></typeparam>
        /// <param name="RealObject"></param>
        public List<Word> Clone<Word>(List<Word> RealObject)
        {
            using (Stream objStream = new MemoryStream())
            {
                //利用 System.Runtime.Serialization序列化与反序列化完成引用对象的复制
                IFormatter formatter = new BinaryFormatter();
                formatter.Serialize(objStream, RealObject);
                objStream.Seek(0, SeekOrigin.Begin);
                return (List<Word>)formatter.Deserialize(objStream);
            }
        }
    }

    /// <summary>
    /// AI 短文预生成结果容器（2026-07-21）。
    /// 在 RecitationSM2 新词学完后由后台线程填充，PushMiniReading 中检查使用。
    /// </summary>
    internal class EssayPreFetchResult
    {
        public bool Success;
        public Model.Ai.EssayResult Result;
        public string Error;
        public List<Word> EssayWords;
    }
}
