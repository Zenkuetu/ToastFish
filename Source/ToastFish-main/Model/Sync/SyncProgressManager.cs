using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ToastFish.Model.Sync
{
    /// <summary>
    /// 多端学习进度同步管理器
    /// 对应 Python sync_progress.py 全部功能
    /// </summary>
    public static class SyncProgressManager
    {
        private static readonly string[] EnglishTables = {
            "CET4_1","CET4_3","CET6_1","CET6_2","CET6_3",
            "Level4_1","Level4luan_2","Level8_1","Level8luan_2",
            "KaoYan_1","KaoYan_2","KaoYan_3","IELTS_3","TOEFL_2",
            "GRE_2","GMAT_3","SAT_2"
        };
        private static readonly string[] JpTables = { "StdJp_Mid" };
        private static readonly string[] GoinTables = { "Goin" };

        /// <summary>
        /// 解析日期字符串
        /// </summary>
        private static DateTime ParseTime(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
            s = s.Trim();
            if (s.Length > 19) s = s.Substring(0, 19);

            string[] formats = { "yyyy/M/d H:m:s", "yyyy-MM-dd H:m:s",
                                 "yyyy/M/d HH:mm:ss", "yyyy-MM-dd HH:mm:ss" };
            foreach (var fmt in formats)
            {
                if (DateTime.TryParseExact(s, fmt, null, System.Globalization.DateTimeStyles.None, out DateTime result))
                    return result;
            }
            return DateTime.MinValue;
        }

        /// <summary>
        /// 获取数据库文件路径
        /// </summary>
        public static string GetDbPath()
        {
            string exeDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            return System.IO.Path.Combine(exeDir, "Resources", "inami.db");
        }

        /// <summary>
        /// 导出学习进度到 JSON 文件
        /// </summary>
        public static string ExportProgress(string dbPath, string outputPath)
        {
            var progress = new Dictionary<string, object>();
            int totalWords = 0;

            using (var db = new SQLiteConnection($"Data Source={dbPath};Version=3"))
            {
                db.Open();

                var meta = new Dictionary<string, string>
                {
                    { "exported_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                    { "exported_from", Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dbPath)) ?? "ToastFish") }
                };
                progress["_meta"] = meta;

                string[] allTables = EnglishTables.Concat(JpTables).Concat(GoinTables).ToArray();

                foreach (string table in allTables)
                {
                    try
                    {
                        // 检查表是否存在
                        using (var checkCmd = db.CreateCommand())
                        {
                            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name";
                            checkCmd.Parameters.AddWithValue("@name", table);
                            if (checkCmd.ExecuteScalar() == null) continue;
                        }

                        // 检查表结构
                        var cols = new List<string>();
                        using (var pragmaCmd = db.CreateCommand())
                        {
                            pragmaCmd.CommandText = $"PRAGMA table_info([{table}])";
                            using (var reader = pragmaCmd.ExecuteReader())
                            {
                                while (reader.Read()) cols.Add(reader["name"].ToString());
                            }
                        }

                        bool hasHeadWord = cols.Contains("headWord");
                        bool hasHiragana = cols.Contains("hiragana");
                        bool hasDifficulty = cols.Contains("difficulty");
                        bool hasDays = cols.Contains("daysBetweenReviews");
                        bool hasScore = cols.Contains("lastScore");
                        bool hasDate = cols.Contains("dateLastReviewed");

                        string idCol = hasHeadWord ? "headWord" : (hasHiragana ? "hiragana" : "romaji");
                        string selectSql = hasHeadWord && hasDifficulty && hasScore && hasDate
                            ? $"SELECT {idCol}, status, difficulty, daysBetweenReviews, lastScore, dateLastReviewed FROM [{table}] WHERE status != 0 OR (dateLastReviewed IS NOT NULL AND dateLastReviewed != '')"
                            : $"SELECT {idCol}, status, 0.3, 3.0, 0, '' FROM [{table}] WHERE status != 0";

                        var words = new Dictionary<string, object>();
                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = selectSql;
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string word = reader[0]?.ToString();
                                    if (string.IsNullOrEmpty(word)) continue;

                                    words[word] = new Dictionary<string, object>
                                    {
                                        { "status", Convert.ToInt32(reader["status"]) },
                                        { "difficulty", hasDifficulty ? Convert.ToDouble(reader["difficulty"]) : 0.3 },
                                        { "daysBetweenReviews", hasDays ? Convert.ToDouble(reader["daysBetweenReviews"]) : 3.0 },
                                        { "lastScore", hasScore ? Convert.ToDouble(reader["lastScore"]) : 0 },
                                        { "dateLastReviewed", hasDate ? (reader["dateLastReviewed"]?.ToString() ?? "") : "" }
                                    };
                                }
                            }
                        }

                        if (words.Count > 0)
                        {
                            progress[table] = words;
                            totalWords += words.Count;
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ExportProgress: skip table {table}: {ex.Message}"); }
                }

                // 导出 Count 表
                try
                {
                    var countData = new Dictionary<string, object>();
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT * FROM Count";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                countData[reader["bookName"].ToString()] = new Dictionary<string, int>
                                {
                                    { "number", Convert.ToInt32(reader["number"]) },
                                    { "current", Convert.ToInt32(reader["current"]) }
                                };
                            }
                        }
                    }
                    progress["_Count"] = countData;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }

                // 导出 Global 表
                try
                {
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT * FROM Global";
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                progress["_Global"] = new Dictionary<string, object>
                                {
                                    { "currentWordNumber", reader["currentWordNumber"]?.ToString() ?? "10" },
                                    { "currentBookName", reader["currentBookName"]?.ToString() ?? "CET4_1" },
                                    { "autoPlay", Convert.ToInt32(reader["autoPlay"]) },
                                    { "EngType", Convert.ToInt32(reader["EngType"]) },
                                    { "autoLog", Convert.ToInt32(reader["autoLog"]) }
                                };
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(progress);
            File.WriteAllText(outputPath, json);

            long sizeKb = new FileInfo(outputPath).Length / 1024;
            return $"[OK] 已导出 {totalWords} 个有进度的词 -> {outputPath} ({sizeKb} KB)";
        }

        /// <summary>
        /// 从 JSON 文件导入进度到数据库（覆盖式）
        /// </summary>
        public static string ImportProgress(string dbPath, string syncFilePath)
        {
            string json = File.ReadAllText(syncFilePath, System.Text.Encoding.UTF8);
            var serializer = new JavaScriptSerializer();
            var progress = serializer.Deserialize<Dictionary<string, object>>(json);

            using (var db = new SQLiteConnection($"Data Source={dbPath};Version=3"))
            {
                db.Open();

                var meta = progress.ContainsKey("_meta") ? (Dictionary<string, object>)progress["_meta"] : null;
                progress.Remove("_meta");

                string metaInfo = meta != null
                    ? $"来自: {meta["exported_at"]} | {meta["exported_from"]}"
                    : "来源未知";

                // 导入 Count 表
                if (progress.ContainsKey("_Count"))
                {
                    var countData = (Dictionary<string, object>)progress["_Count"];
                    progress.Remove("_Count");
                    foreach (var kv in countData)
                    {
                        var data = (Dictionary<string, object>)kv.Value;
                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = "INSERT OR REPLACE INTO Count(bookName, number, current) VALUES(@name, @num, @cur)";
                            cmd.Parameters.AddWithValue("@name", kv.Key);
                            cmd.Parameters.AddWithValue("@num", Convert.ToInt32(data["number"]));
                            cmd.Parameters.AddWithValue("@cur", Convert.ToInt32(data["current"]));
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                // 导入 Global 表
                if (progress.ContainsKey("_Global"))
                {
                    var globalData = (Dictionary<string, object>)progress["_Global"];
                    progress.Remove("_Global");
                    using (var cmd = db.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM Global";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = @"INSERT INTO Global VALUES(@wn, @bn, @ap, @et, @al)";
                        cmd.Parameters.AddWithValue("@wn", globalData.ContainsKey("currentWordNumber") ? globalData["currentWordNumber"]?.ToString() : "10");
                        cmd.Parameters.AddWithValue("@bn", globalData.ContainsKey("currentBookName") ? globalData["currentBookName"]?.ToString() : "CET4_1");
                        cmd.Parameters.AddWithValue("@ap", globalData.ContainsKey("autoPlay") ? Convert.ToInt32(globalData["autoPlay"]) : 1);
                        cmd.Parameters.AddWithValue("@et", globalData.ContainsKey("EngType") ? Convert.ToInt32(globalData["EngType"]) : 2);
                        cmd.Parameters.AddWithValue("@al", globalData.ContainsKey("autoLog") ? Convert.ToInt32(globalData["autoLog"]) : 1);
                        cmd.ExecuteNonQuery();
                    }
                }

                int total = 0;
                foreach (var tableKv in progress)
                {
                    string table = tableKv.Key;
                    var words = (Dictionary<string, object>)tableKv.Value;

                    // 检查表是否存在
                    using (var checkCmd = db.CreateCommand())
                    {
                        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name";
                        checkCmd.Parameters.AddWithValue("@name", table);
                        if (checkCmd.ExecuteScalar() == null) continue;
                    }

                    // 检测列
                    var cols = new List<string>();
                    using (var pragmaCmd = db.CreateCommand())
                    {
                        pragmaCmd.CommandText = $"PRAGMA table_info([{table}])";
                        using (var reader = pragmaCmd.ExecuteReader())
                        {
                            while (reader.Read()) cols.Add(reader["name"].ToString());
                        }
                    }

                    bool hasHeadWord = cols.Contains("headWord");
                    bool hasHiragana = cols.Contains("hiragana");
                    bool hasRomaji = cols.Contains("romaji");
                    bool hasDifficulty = cols.Contains("difficulty");
                    bool hasScore = cols.Contains("lastScore");
                    bool hasDate = cols.Contains("dateLastReviewed");
                    bool hasDays = cols.Contains("daysBetweenReviews");

                    string idCol = hasHeadWord ? "headWord" : (hasHiragana ? "hiragana" : (hasRomaji ? "romaji" : ""));
                    if (string.IsNullOrEmpty(idCol)) continue;

                    foreach (var wordKv in words)
                    {
                        var data = (Dictionary<string, object>)wordKv.Value;
                        var setParts = new List<string> { "status = @status" };
                        using (var cmd = db.CreateCommand())
                        {
                            cmd.Parameters.AddWithValue("@status", Convert.ToInt32(data["status"]));
                            if (hasDifficulty)
                            {
                                setParts.Add("difficulty = @diff");
                                cmd.Parameters.AddWithValue("@diff", Convert.ToDouble(data["difficulty"]));
                            }
                            if (hasDays)
                            {
                                setParts.Add("daysBetweenReviews = @days");
                                cmd.Parameters.AddWithValue("@days", Convert.ToDouble(data.ContainsKey("daysBetweenReviews") ? data["daysBetweenReviews"] : 3.0));
                            }
                            if (hasScore)
                            {
                                setParts.Add("lastScore = @score");
                                cmd.Parameters.AddWithValue("@score", Convert.ToDouble(data["lastScore"]));
                            }
                            if (hasDate)
                            {
                                setParts.Add("dateLastReviewed = @date");
                                cmd.Parameters.AddWithValue("@date", data.ContainsKey("dateLastReviewed") ? (data["dateLastReviewed"]?.ToString() ?? "") : "");
                            }
                            cmd.Parameters.AddWithValue("@word", wordKv.Key);

                            cmd.CommandText = $"UPDATE [{table}] SET {string.Join(", ", setParts)} WHERE [{idCol}] = @word";
                            cmd.ExecuteNonQuery();
                            total++;
                        }
                    }
                }

                // 重新计算 Count
                foreach (var tableKv in progress)
                {
                    try
                    {
                        using (var cmd = db.CreateCommand())
                        {
                            cmd.CommandText = $"SELECT COUNT(*) FROM [{tableKv.Key}] WHERE status != 0";
                            int current = Convert.ToInt32(cmd.ExecuteScalar());
                            cmd.CommandText = "UPDATE Count SET current = @cur WHERE bookName = @bn";
                            cmd.Parameters.AddWithValue("@cur", current);
                            cmd.Parameters.AddWithValue("@bn", tableKv.Key);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                }
            }

            return $"[OK] 已更新进度";
        }

        /// <summary>
        /// 双向合并两个数据库：对每个词，取 dateLastReviewed 较新的记录
        /// </summary>
        public static string MergeDatabases(string dbPathA, string dbPathB)
        {
            using (var dbA = new SQLiteConnection($"Data Source={dbPathA};Version=3"))
            using (var dbB = new SQLiteConnection($"Data Source={dbPathB};Version=3"))
            {
                dbA.Open();
                dbB.Open();

                string[] allTables = EnglishTables.Concat(JpTables).Concat(GoinTables).ToArray();
                int totalUpdated = 0;

                foreach (string table in allTables)
                {
                    try
                    {
                        // 检查 B 中表存在
                        using (var checkCmd = dbB.CreateCommand())
                        {
                            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@t";
                            checkCmd.Parameters.AddWithValue("@t", table);
                            if (checkCmd.ExecuteScalar() == null) continue;
                        }
                        using (var checkCmd = dbA.CreateCommand())
                        {
                            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@t";
                            checkCmd.Parameters.AddWithValue("@t", table);
                            if (checkCmd.ExecuteScalar() == null) continue;
                        }

                        // 检测列
                        var aCols = new HashSet<string>();
                        using (var cmd = dbA.CreateCommand())
                        {
                            cmd.CommandText = $"PRAGMA table_info([{table}])";
                            using (var r = cmd.ExecuteReader())
                            { while (r.Read()) aCols.Add(r["name"].ToString()); }
                        }

                        bool hasDiffA = aCols.Contains("difficulty");
                        bool hasDateA = aCols.Contains("dateLastReviewed");
                        bool hasHeadA = aCols.Contains("headWord");
                        bool hasHiraA = aCols.Contains("hiragana");
                        bool hasRomA = aCols.Contains("romaji");

                        string idCol = hasHeadA ? "headWord" : (hasHiraA ? "hiragana" : (hasRomA ? "romaji" : ""));
                        if (string.IsNullOrEmpty(idCol)) continue;

                        // 读取 B 中有进度的词
                        var bWords = new Dictionary<string, (int status, double diff, double days, double score, string date)>();
                        using (var cmd = dbB.CreateCommand())
                        {
                            cmd.CommandText = hasDiffA && hasDateA
                                ? $"SELECT [{idCol}], status, difficulty, daysBetweenReviews, lastScore, dateLastReviewed FROM [{table}] WHERE status != 0 OR (dateLastReviewed IS NOT NULL AND dateLastReviewed != '')"
                                : $"SELECT [{idCol}], status, 0.3, 3.0, 0, '' FROM [{table}] WHERE status != 0";
                            using (var r = cmd.ExecuteReader())
                            {
                                while (r.Read())
                                {
                                    string word = r[0]?.ToString()?.Trim();
                                    if (string.IsNullOrEmpty(word)) continue;
                                    bWords[word] = (
                                        Convert.ToInt32(r["status"]),
                                        Convert.ToDouble(r["difficulty"] ?? 0.3),
                                        Convert.ToDouble(r["daysBetweenReviews"] ?? 3.0),
                                        Convert.ToDouble(r["lastScore"] ?? 0),
                                        r["dateLastReviewed"]?.ToString() ?? ""
                                    );
                                }
                            }
                        }

                        if (bWords.Count == 0) continue;

                        foreach (var kv in bWords)
                        {
                            // 查 A 中的对应记录
                            string aDate = "";
                            using (var cmd = dbA.CreateCommand())
                            {
                                cmd.CommandText = hasDateA
                                    ? $"SELECT dateLastReviewed FROM [{table}] WHERE [{idCol}] = @w"
                                    : $"SELECT '' FROM [{table}] WHERE [{idCol}] = @w";
                                cmd.Parameters.AddWithValue("@w", kv.Key);
                                var result = cmd.ExecuteScalar();
                                aDate = result?.ToString() ?? "";
                            }

                            DateTime aTime = ParseTime(aDate);
                            DateTime bTime = ParseTime(kv.Value.date);

                            if (bTime > aTime)
                            {
                                using (var cmd = dbA.CreateCommand())
                                {
                                    if (hasDiffA && hasDateA)
                                    {
                                        cmd.CommandText = $"UPDATE [{table}] SET status=@s, difficulty=@d, daysBetweenReviews=@i, lastScore=@sc, dateLastReviewed=@dlr WHERE [{idCol}]=@w";
                                        cmd.Parameters.AddWithValue("@s", kv.Value.status);
                                        cmd.Parameters.AddWithValue("@d", kv.Value.diff);
                                        cmd.Parameters.AddWithValue("@i", kv.Value.days);
                                        cmd.Parameters.AddWithValue("@sc", kv.Value.score);
                                        cmd.Parameters.AddWithValue("@dlr", kv.Value.date);
                                    }
                                    else
                                    {
                                        cmd.CommandText = $"UPDATE [{table}] SET status=@s WHERE [{idCol}]=@w";
                                        cmd.Parameters.AddWithValue("@s", kv.Value.status);
                                    }
                                    cmd.Parameters.AddWithValue("@w", kv.Key);
                                    cmd.ExecuteNonQuery();
                                    totalUpdated++;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                }

                // 更新 Count
                foreach (string table in allTables)
                {
                    try
                    {
                        using (var cmd = dbA.CreateCommand())
                        {
                            cmd.CommandText = $"SELECT COUNT(*) FROM [{table}] WHERE status != 0";
                            int cur = Convert.ToInt32(cmd.ExecuteScalar());
                            cmd.CommandText = "UPDATE Count SET current = @c WHERE bookName = @b";
                            cmd.Parameters.AddWithValue("@c", cur);
                            cmd.Parameters.AddWithValue("@b", table);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
                }
            }

            return $"[OK] 从 B 合并到 A";
        }

        /// <summary>
        /// 与云端数据库双向同步
        /// </summary>
        public static string AutoSync(string localDb, string cloudDb)
        {
            if (!File.Exists(cloudDb))
            {
                File.Copy(localDb, cloudDb);
                return "[OK] 云端无文件，已上传本地数据库";
            }

            // 备份
            File.Copy(localDb, localDb + ".pre_sync", true);
            File.Copy(cloudDb, cloudDb + ".pre_sync", true);

            MergeDatabases(localDb, cloudDb);
            MergeDatabases(cloudDb, localDb);

            return $"[OK] 双向同步完成\n     本地: {localDb}\n     云端: {cloudDb}\n     备份: .pre_sync";
        }
    }
}
