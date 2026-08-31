using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;

namespace ToastFish.Model.StudyLog
{
    /// <summary>
    /// 每日学习统计记录
    /// </summary>
    public class StudyStats
    {
        public int Total { get; set; }
        public int Easy { get; set; }
        public int Good { get; set; }
        public int Hard { get; set; }
        public int Again { get; set; }
        public int LearnedTotal { get; set; }
        public int WordTotal { get; set; }
        public string BookName { get; set; } = "CET6_3";
        public int Streak { get; set; }
    }

    /// <summary>
    /// 学习日志管理器
    /// 对应 Python ToastFishLauncher.py 中的 StudyLog、统计计算 和 sm2plus_engine.py 的 stats 命令
    /// </summary>
    public static class StudyLogManager
    {
        private static readonly string[] EnglishTables = {
            "CET4_1","CET4_3","CET6_1","CET6_2","CET6_3",
            "Level4_1","Level4luan_2","Level8_1","Level8luan_2",
            "KaoYan_1","KaoYan_2","IELTS_3","TOEFL_2",
            "GRE_2","GMAT_3","SAT_2"
        };

        /// <summary>
        /// 确保 StudyLog 表存在
        /// </summary>
        public static void EnsureStudyLogTable(SQLiteConnection db)
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS StudyLog (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    date TEXT NOT NULL UNIQUE,
                    bookName TEXT NOT NULL,
                    wordsReviewed INTEGER DEFAULT 0,
                    scoreEASY INTEGER DEFAULT 0,
                    scoreGOOD INTEGER DEFAULT 0,
                    scoreHARD INTEGER DEFAULT 0,
                    scoreAGAIN INTEGER DEFAULT 0
                )";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 记录今日学习数据到 StudyLog（累加方式，支持多轮学习）
        /// </summary>
        public static void LogStudySession(SQLiteConnection db, string bookName, int total,
            int easy, int good, int hard, int again)
        {
            EnsureStudyLogTable(db);
            string today = DateTime.Today.ToString("yyyy-MM-dd");

            using (var cmd = db.CreateCommand())
            {
                // 先检查今天是否已有记录
                cmd.CommandText = "SELECT COUNT(*) FROM StudyLog WHERE date = @date";
                cmd.Parameters.AddWithValue("@date", today);
                int existing = Convert.ToInt32(cmd.ExecuteScalar());

                // 清除参数，避免后续 AddWithValue 因同名参数抛 ArgumentException
                cmd.Parameters.Clear();

                if (existing > 0)
                {
                    // 累加
                    cmd.CommandText = @"UPDATE StudyLog SET
                        wordsReviewed = wordsReviewed + @total,
                        scoreEASY = scoreEASY + @easy,
                        scoreGOOD = scoreGOOD + @good,
                        scoreHARD = scoreHARD + @hard,
                        scoreAGAIN = scoreAGAIN + @again
                        WHERE date = @date";
                }
                else
                {
                    // 新插入
                    cmd.CommandText = @"INSERT INTO StudyLog
                        (date, bookName, wordsReviewed, scoreEASY, scoreGOOD, scoreHARD, scoreAGAIN)
                        VALUES (@date, @book, @total, @easy, @good, @hard, @again)";
                    cmd.Parameters.AddWithValue("@book", bookName);
                }
                cmd.Parameters.AddWithValue("@date", today);
                cmd.Parameters.AddWithValue("@total", total);
                cmd.Parameters.AddWithValue("@easy", easy);
                cmd.Parameters.AddWithValue("@good", good);
                cmd.Parameters.AddWithValue("@hard", hard);
                cmd.Parameters.AddWithValue("@again", again);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 计算今日学习统计
        /// 从数据库获取：今日复习数、总进度、连续天数
        /// </summary>
        public static StudyStats ComputeTodayStats(SQLiteConnection db, int updatedCount,
            int easyCount, int goodCount, int hardCount, int againCount)
        {
            var stats = new StudyStats
            {
                Total = updatedCount,
                Easy = easyCount,
                Good = goodCount,
                Hard = hardCount,
                Again = againCount
            };

            try
            {
                // 获取当前词库
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT currentBookName FROM Global";
                    var result = cmd.ExecuteScalar();
                    if (result != null)
                        stats.BookName = result.ToString();
                }

                // 如果今日 StudyLog 已有记录，优先使用
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT wordsReviewed, scoreEASY, scoreGOOD, scoreHARD, scoreAGAIN FROM StudyLog WHERE date = @date";
                    cmd.Parameters.AddWithValue("@date", today);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            stats.Total = Convert.ToInt32(reader["wordsReviewed"]);
                            stats.Easy = Convert.ToInt32(reader["scoreEASY"]);
                            stats.Good = Convert.ToInt32(reader["scoreGOOD"]);
                            stats.Hard = Convert.ToInt32(reader["scoreHARD"]);
                            stats.Again = Convert.ToInt32(reader["scoreAGAIN"]);
                        }
                    }
                }

                // 当前词库总词数和已学数
                try
                {
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT COUNT(*) FROM [{stats.BookName}]";
                        stats.WordTotal = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT COUNT(*) FROM [{stats.BookName}] WHERE status = 5";
                        stats.LearnedTotal = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                }
                catch
                {
                    stats.WordTotal = 0;
                    stats.LearnedTotal = 0;
                }

                // 连续学习天数（从 StudyLog 表向前回溯）
                stats.Streak = GetStreakDays(db);
            }
            catch { }

            return stats;
        }

        /// <summary>
        /// 计算连续学习天数
        /// 从 StudyLog 表读取日期，向前回溯连续天数
        /// </summary>
        public static int GetStreakDays(SQLiteConnection db)
        {
            try
            {
                var dates = new List<DateTime>();
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT date FROM StudyLog ORDER BY date DESC";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string dateStr = reader.GetString(0);
                            if (DateTime.TryParse(dateStr, out DateTime parsed))
                                dates.Add(parsed.Date);
                        }
                    }
                }

                if (dates.Count == 0) return 0;

                // 从最近的学习日期开始往前数，而非强制要求今天有记录
                DateTime checkDate = dates[0].Date;  // 最新记录日期
                // 如果最新记录是今天之前且差距为 1 天（昨天），则从昨天开始算
                if (checkDate < DateTime.Today && (DateTime.Today - checkDate).Days == 1)
                    checkDate = DateTime.Today;  // 允许今天未学但昨天学了 → 从今天开始倒推

                int streak = 0;
                foreach (var d in dates)
                {
                    if (d.Date == checkDate)
                    {
                        streak++;
                        checkDate = checkDate.AddDays(-1);
                    }
                    else
                    {
                        break;
                    }
                }

                return streak;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 学习后扫描所有英语词库，统计今日得分分布
        /// 对应 Python post_learn() 中的分数统计逻辑
        /// </summary>
        public static void CollectAndLogStudySession(SQLiteConnection db)
        {
            try
            {
                EnsureStudyLogTable(db);

                int easy = 0, good = 0, hard = 0, again = 0;
                var scoreMap = new Dictionary<double, string>
                {
                    { 1.0, "easy" }, { 0.8, "good" }, { 0.6, "hard" }, { 0.4, "again" }
                };

                foreach (string table in EnglishTables)
                {
                    try
                    {
                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = $@"
                                SELECT lastScore, COUNT(*)
                                FROM [{table}]
                                WHERE status = 5
                                  AND lastScore > 0
                                  AND dateLastReviewed IS NOT NULL
                                  AND dateLastReviewed != ''
                                  AND dateLastReviewed LIKE @today || '%'
                                GROUP BY lastScore";
                            cmd.Parameters.AddWithValue("@today", DateTime.Today.ToString("yyyy/M/d"));
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    double score = Convert.ToDouble(reader[0]);
                                    int count = Convert.ToInt32(reader[1]);
                                    string key = scoreMap.ContainsKey(Math.Round(score, 1))
                                        ? scoreMap[Math.Round(score, 1)] : "good";

                                    switch (key)
                                    {
                                        case "easy": easy += count; break;
                                        case "good": good += count; break;
                                        case "hard": hard += count; break;
                                        case "again": again += count; break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                int total = easy + good + hard + again;
                if (total > 0)
                {
                    // 获取当前词库
                    string bookName = "CET6_3";
                    try
                    {
                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = "SELECT currentBookName FROM Global";
                            var result = cmd.ExecuteScalar();
                            if (result != null) bookName = result.ToString();
                        }
                    }
                    catch { }

                    LogStudySession(db, bookName, total, easy, good, hard, again);
                }
            }
            catch { }
        }

        /// <summary>
        /// 查看学习统计（控制台输出，对应 sm2plus_engine.py 的 stats 命令）
        /// </summary>
        public static void PrintStats(string dbPath)
        {
            using (var db = new SQLiteConnection($"Data Source={dbPath};Version=3"))
            {
                db.Open();

                Console.WriteLine(new string('=', 70));
                Console.WriteLine(string.Format("{0,-20} {1,6} {2,6} {3,8} {4,10}",
                    "词库", "总数", "已学", "难度均值", "间隔均值(天)"));
                Console.WriteLine(new string('-', 70));

                int totalWords = 0, totalLearned = 0;

                foreach (string table in EnglishTables)
                {
                    try
                    {
                        int count = 0, learned = 0;
                        double? avgDiff = null, avgInterval = null;

                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
                            count = Convert.ToInt32(cmd.ExecuteScalar());
                        }
                        if (count == 0) continue;

                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = $"SELECT COUNT(*) FROM [{table}] WHERE status = 5";
                            learned = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = $"SELECT AVG(difficulty), AVG(daysBetweenReviews) FROM [{table}] WHERE status = 5";
                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    avgDiff = reader.IsDBNull(0) ? (double?)null : reader.GetDouble(0);
                                    avgInterval = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
                                }
                            }
                        }

                        totalWords += count;
                        totalLearned += learned;

                        string diffStr = avgDiff.HasValue ? $"{avgDiff.Value:F3}" : "N/A";
                        string intvStr = avgInterval.HasValue ? $"{avgInterval.Value:F1}" : "N/A";

                        Console.WriteLine(string.Format("  {0,-18} {1,6} {2,6} {3,8} {4,10}",
                            table, count, learned, diffStr, intvStr));
                    }
                    catch { }
                }

                Console.WriteLine(new string('-', 70));
                Console.WriteLine(string.Format("  {0,-18} {1,6} {2,6}", "合计", totalWords, totalLearned));
                double pct = totalWords > 0 ? 100.0 * totalLearned / totalWords : 0;
                Console.WriteLine($"\n  学习进度: {totalLearned}/{totalWords} ({pct:F1}%)");

                try
                {
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT currentBookName, currentWordNumber FROM Global";
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                                Console.WriteLine($"  当前词库: {reader.GetString(0)}, 每次单词数: {reader.GetString(1)}");
                        }
                    }
                }
                catch { }
            }
        }
    }
}
