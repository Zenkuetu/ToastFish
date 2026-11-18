using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;
using ToastFish.Model.Ai;
using ToastFish.Model.SqliteControl;
using ToastFish.View;

namespace ToastFish.Model.PushControl
{
    /// <summary>
    /// Alt+R 短文练习（#144，2026-11-18）：独立于学习流程。
    /// 按当前词库难度从正式出版物节选/改编一篇短文，生成后复用现有弹窗
    /// （经典阅读 / 15选10 / 20空四选一 / 英译汉），结果写 EssayLog 供仪表盘复盘。
    /// </summary>
    internal static class PassagePractice
    {
        public const int KindReading = 0;
        public const int KindCloze10 = 1;
        public const int KindCloze20 = 2;
        public const int KindTrans = 3;
        public const int KindRandom = 4;

        private static int _busy;   // 0=空闲 1=正在选题/生成：连按 Alt+R 不会叠出好几份短文

        /// <summary>Alt+R 入口。热键回调跑在 WPF UI 线程（#140 的坑），这里只判断和派发，绝不等 AI。</summary>
        public static void Begin()
        {
            if (PushWords.IsLearningActive)
            {
                NotificationForm.ShowMessage("短文练习", "本轮学习还在进行，学完再按 Alt+R。");
                return;
            }
            if (NotificationForm.HasAnyLivePopup)
            {
                NotificationForm.ShowMessage("短文练习", "先把这个弹窗处理完，再按 Alt+R。");
                return;
            }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                NotificationForm.ShowMessage("短文练习", "上一份短文还在生成，请稍候…");
                return;
            }

            string level = EssayGenerator.GetBookLevel(Select.TABLE_NAME);
            var picker = NotificationForm.ShowWithButtons(
                "短文练习",
                "当前词库：" + level + "。挑一种题型，AI 会按这个难度从正式出版物节选并改编，题目与选项也由 AI 编写。",
                new[] { "经典阅读（4 道单选）", "15选10 选词填空", "20空四选一 完形填空", "英译汉（5 句划线）", "随机一种" },
                0, true);

            picker.ContinueWith(t =>
            {
                int kind = t.Result;
                if (kind < 0) { Interlocked.Exchange(ref _busy, 0); return; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Generate(kind, level); }
                    catch (Exception e) { Fail("生成异常：" + e.Message); }
                    finally { Interlocked.Exchange(ref _busy, 0); }
                });
            });
        }

        private static void Generate(int kind, string level)
        {
            if (kind == KindRandom) kind = new SafeRandom().Next(4);

            string book = Select.TABLE_NAME;
            List<string> words = PickHighlightWords(20);   // 只用于高亮和记录，不要求 AI 全部用上
            string error;
            var jss = new JavaScriptSerializer();

            if (kind == KindReading)
            {
                EssayGenerator.PracticePassage p;
                if (!EssayGenerator.TryGeneratePracticeReading(book, words, out p, out error)) { Fail(error); return; }
                if (!CanShow()) return;
                string qJson = jss.Serialize(p.Questions.ConvertAll(q => new
                {
                    q = q.Question, choices = q.Choices, answer = q.Answer
                }));
                string answers = "[]";
                NotificationForm.ShowEssayPopup(p.EssayEN, p.EssayCN, words, null, p.Questions,
                    r => Finish("阅读", words, p.EssayEN, p.EssayCN, qJson, r, answers, p.Source, book),
                    0, j => answers = j, null, null, false, "短文练习 · " + SourceOf(p.Source));
                return;
            }

            if (kind == KindCloze10)
            {
                ClozeResult c;
                string src;
                if (!EssayGenerator.TryGeneratePracticeCloze(book, words, out c, out src, out error)) { Fail(error); return; }
                if (!CanShow()) return;
                string qJson = jss.Serialize(new
                {
                    type = "cloze",
                    candidates = c.Candidates.ConvertAll(x => new { word = x.Word, blank = x.Blank })
                });
                string answers = "[]";
                NotificationForm.ShowClozePopup(c, words,
                    r => Finish("15选10", words, c.Text, c.TextCN, qJson, r, answers, src, book),
                    0, j => answers = j);
                return;
            }

            if (kind == KindCloze20)
            {
                Cloze4Result c;
                string src;
                if (!EssayGenerator.TryGeneratePracticeCloze4(book, words, out c, out src, out error)) { Fail(error); return; }
                if (!CanShow()) return;
                string qJson = jss.Serialize(new
                {
                    type = "cloze4",
                    questions = c.Questions.ConvertAll(q => new { blank = q.Blank, options = q.Options, answer = q.Answer })
                });
                string answers = "[]";
                NotificationForm.ShowCloze4ChoicePopup(c, words,
                    r => Finish("20空四选一", words, c.Text, c.TextCN, qJson, r, answers, src, book),
                    0, j => answers = j);
                return;
            }

            // 英译汉
            TransPassage t;
            string tsrc;
            if (!EssayGenerator.TryGeneratePracticeTrans(book, words, out t, out tsrc, out error)) { Fail(error); return; }
            if (!CanShow()) return;
            string tJson = jss.Serialize(new
            {
                type = "trans",
                items = t.Items.ConvertAll(it => new { s = it.Sentence, refer = it.Reference, points = it.Points })
            });
            string tAnswers = "[]";
            NotificationForm.ShowEssayPopup(t.En, "", words, null, null,
                r => Finish("英译汉", words, t.En, "", tJson, r, tAnswers, tsrc, book),
                0, j => tAnswers = j, null, t.Items, true, "短文练习 · " + SourceOf(tsrc));
        }

        /// <summary>生成完成时再确认一次：万一用户这几十秒里按了 Alt+Q 开始学习，就不要再弹（会顶掉学习弹窗）。</summary>
        private static bool CanShow()
        {
            if (!PushWords.IsLearningActive) return true;
            NotificationForm.ShowMessage("短文练习", "本轮学习已经开始，这份短文先不弹了（学完再按 Alt+R）。");
            return false;
        }

        /// <summary>弹窗收尾：落 EssayLog（仪表盘按 type 复盘）+ 一条带分数和出处的提示。</summary>
        private static void Finish(string kindLabel, List<string> words, string en, string cn,
            string questionsJson, int result, string answers, string source, string book)
        {
            int attempted = result < 0 ? 0 : (result >> 16) & 0xFFFF;
            int correct = result < 0 ? 0 : result & 0xFFFF;

            // 落库丢后台：回调在 UI 线程上，别让 SQLite 写盘卡住鼠标
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var jss = new JavaScriptSerializer();
                    Select.InsertEssayLog(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), book,
                        words.Count, jss.Serialize(words), en, cn ?? "", questionsJson,
                        attempted, correct, AiConfig.ModelMode, answers);
                }
                catch (Exception e) { Debug.WriteLine("#144 落库失败: " + e.Message); }
            });

            string msg;
            if (attempted > 0)
                msg = kindLabel + "完成  ·  答对 " + correct + "/" + attempted + "\n出处：" + SourceOf(source);
            else
                msg = "这次没有作答。出处：" + SourceOf(source);
            NotificationForm.ShowMessage("短文练习", msg, 8000);
        }

        private static string SourceOf(string source)
        {
            return string.IsNullOrEmpty(source) ? "正式出版物（改编）" : source;
        }

        private static void Fail(string error)
        {
            NotificationForm.ShowMessage("短文练习",
                "生成失败：" + (string.IsNullOrEmpty(error) ? "未知错误" : error) +
                "\n检查「参数设置 → AI 服务商」里的 Key，或稍后再试。", 8000);
        }

        /// <summary>从当前词书随机挑已学过的词（只用于高亮）；不足 5 个就不挑，短文照常生成。</summary>
        private static List<string> PickHighlightWords(int count)
        {
            var list = new List<string>();
            try
            {
                using (var db = new SQLiteConnection(@"Data Source=" + PathHelper.ExeDir +
                        @"\Resources\inami.db;Version=3"))
                {
                    db.Open();
                    using (var cmd = new SQLiteCommand(
                        "SELECT [headWord] FROM [" + Select.TABLE_NAME + "] WHERE status != 0 ORDER BY RANDOM() LIMIT " + count, db))
                    using (var rd = cmd.ExecuteReader())
                        while (rd.Read())
                        {
                            string w = rd.IsDBNull(0) ? null : rd.GetString(0);
                            if (!string.IsNullOrEmpty(w)) list.Add(w);
                        }
                }
            }
            catch (Exception e) { Debug.WriteLine("#144 取高亮词失败: " + e.Message); }
            if (list.Count < 5) list.Clear();
            return list;
        }
    }
}
