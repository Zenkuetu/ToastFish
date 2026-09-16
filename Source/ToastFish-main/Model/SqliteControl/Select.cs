using Dapper;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using ToastFish.Model.SM2plus;

namespace ToastFish.Model.SqliteControl
{
    public class Select : IDisposable
    {
        private static readonly Random _random = new Random();

        public Select()
        {
            DataBase = ConnectToDatabase();
            DataBase.Open();
        }

        public void Dispose()
        {
            DataBase?.Close();
            DataBase?.Dispose();
            DataBase = null;
        }

        public static string TABLE_NAME = "CET4_1";  // 当前书籍名字
        public static int WORD_NUMBER = 10;  // 当前单词数量
        public static int ENG_TYPE = 2;  // 英语类型1：美语，2：英语
        public static int AUTO_PLAY = 1;  // 英语自动发音
        public static int AUTO_LOG  = 1;  // 英语自动发音
        public SQLiteConnection DataBase;
        public IEnumerable<Word> AllWordList;
        public IEnumerable<JpWord> AllJpWordList;
        public IEnumerable<BookCount> CountList;
        List<Card> NewCardLst = new List<Card>();
        public List<Card> LearningCardLst = new List<Card>();
        List<Card> ReviewedCardLst = new List<Card>();


        #region 更新与链接
        /// <summary>
        /// 连接数据库
        /// </summary>
        SQLiteConnection ConnectToDatabase()
        {
            //var databasePath = @"Data Source=./Resources/inami.db;Version=3";
            //var databasePath = @"Data Source="+System.IO.Directory.GetCurrentDirectory() + @"\Resources\inami.db;Version=3";
            string strExeFilePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string databasePath = @"Data Source=" + System.IO.Path.GetDirectoryName(strExeFilePath) + @"\Resources\inami.db;Version=3";
            return new SQLiteConnection(databasePath);
        }

        /// <summary>
        /// 标记单词已背过
        /// </summary>
        public void UpdateWord(int WordRank)
        {
            SQLiteCommand Update = DataBase.CreateCommand();
            Update.CommandText = "UPDATE " + TABLE_NAME + " SET status = 1 WHERE wordRank = " + WordRank;
            Update.ExecuteNonQuery();
        }

        /// <summary>
        /// 同步当前表的 Count 记录（current=已学数, number=总词数）
        /// 每次学习完成后调用，确保进度计数实时准确
        /// </summary>
        public void SyncCountTable()
        {
            try
            {
                SQLiteCommand cmd = DataBase.CreateCommand();
                cmd.CommandText = $"UPDATE Count SET current = (SELECT COUNT(*) FROM [{TABLE_NAME}] WHERE status != 0), number = (SELECT COUNT(*) FROM [{TABLE_NAME}]) WHERE bookName = '{TABLE_NAME}'";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SyncCountTable error: {ex.Message}");
            }
        }

        //重置单词记录
        public void ResetTableCount()
        {
            String cmdtext = $"UPDATE  {TABLE_NAME} SET status = 0 ";
            SQLiteCommand Update = DataBase.CreateCommand();
            Update.CommandText = cmdtext;
            Update.ExecuteNonQuery();
        }

        /// <summary>
        /// 更新CountTable的单词记录
        /// </summary>
        /// 
        public void UpdateTableCount()
        {
            String cmdtext = $"Select status from {TABLE_NAME}";
            SQLiteCommand Update = DataBase.CreateCommand();
            Update.CommandText = cmdtext;
            var dr = Update.ExecuteReader();
            List<string> statusLst = new List<string>();
            int Count = 0;
            int value = -1;
            while (dr.Read())//loop through the various columns and their info
            {
                var rawvalue = dr.GetValue(0);//0:cid;1:name; 2:type;3:notnull;4:dflt_value;5:pk

                string type = rawvalue.GetType().Name;
                if (type.Equals("String", StringComparison.OrdinalIgnoreCase))
                    value = int.Parse((string)rawvalue);
                else
                    value = int.Parse(rawvalue.ToString());
                if (value != 0)
                    Count++;
            }
            dr.Close();
            Update.CommandText = "UPDATE Count SET current = " + Count.ToString() + " WHERE bookName = '" + TABLE_NAME + "'";
            Update.ExecuteNonQuery();
        }

        //increase count by 1
        public void UpdateCount()
        {
            BookCount Temp = new BookCount();
            CountList = DataBase.Query<BookCount>($"select * from Count where bookName = '{TABLE_NAME}'", Temp);
            var CountArray = CountList.ToArray();
            foreach (var OneCount in CountArray)
            {
                if (OneCount.bookName == TABLE_NAME)
                {
                    int Count = OneCount.current + 1;
                    if (OneCount.bookName == "Goin")
                        Count %= 104;
                    SQLiteCommand Update = DataBase.CreateCommand();
                    Update.CommandText = "UPDATE Count SET current = " + Count.ToString() + " WHERE bookName = '" + TABLE_NAME + "'";
                    Update.ExecuteNonQuery();
                    break;
                }
            }
        }

        public void LoadGlobalConfig()
        {
            String cmdtext = $"PRAGMA table_info(Global)";
            SQLiteCommand Update = DataBase.CreateCommand();
            Update.CommandText = cmdtext;
            var dr = Update.ExecuteReader();
            List<string> HeadTileList = new List<string>();
            while (dr.Read())//loop through the various columns and their info
            {
                string name = (string)dr.GetValue(1);//0:cid;1:name; 2:type;3:notnull;4:dflt_value;5:pk
                HeadTileList.Add(name);
                //Console.WriteLine(name);
            }
            dr.Close();
            if (HeadTileList.Contains("EngType") == false)
            {
                Update.CommandText = $"ALTER TABLE Global ADD COLUMN EngType INTEGER NOT NULL DEFAULT {ENG_TYPE}";
                Update.ExecuteNonQuery();
            }
            if (HeadTileList.Contains("autoLog") == false)
            {
                Update.CommandText = $"ALTER TABLE Global ADD COLUMN autoLog INTEGER NOT NULL DEFAULT {AUTO_LOG}";
                Update.ExecuteNonQuery();
            }
            // AI 短文门控累积列（2026-09-16 新增），幂等 ALTER
            if (HeadTileList.Contains("pendingEssayWords") == false)
            {
                Update.CommandText = "ALTER TABLE Global ADD COLUMN pendingEssayWords TEXT NOT NULL DEFAULT ''";
                Update.ExecuteNonQuery();
            }
            Global Temp = new Global();
            var GlobalVariable = DataBase.Query<Global>("select * from Global", Temp).ToArray();
            if (GlobalVariable.Length == 0)
            {
                // Global 表为空，使用默认值
                return;
            }
            WORD_NUMBER = int.Parse(GlobalVariable[0].currentWordNumber);
            TABLE_NAME = GlobalVariable[0].currentBookName;
            AUTO_PLAY = GlobalVariable[0].autoPlay;
            ENG_TYPE = GlobalVariable[0].EngType;
            AUTO_LOG = GlobalVariable[0].autoLog;
        }

        public void UpdateGlobalConfig()
        {
            SQLiteCommand Update = DataBase.CreateCommand();
            Update.CommandText = $"UPDATE Global SET currentWordNumber ='{WORD_NUMBER}'" +
                $", currentBookName = '{TABLE_NAME}'" +
                $", autoPlay = '{AUTO_PLAY}'" +
                $", EngType = '{ENG_TYPE}' " +
                $", autoLog = '{AUTO_LOG}'";
            Update.ExecuteNonQuery();
        }

        public void UpdateBookName(string TableName)
        {
            SQLiteCommand Update = DataBase.CreateCommand();
            Update.CommandText = "UPDATE Global SET currentBookName = '" + TableName + "'";
            Update.ExecuteNonQuery();
            //Global Temp = new Global();
            //var GlobalVariable = DataBase.Query<Global>("select * from Global", Temp).ToArray();
        }

        public void UpdateNumber(int WordNumber)
        {
            SQLiteCommand Update = DataBase.CreateCommand();
            Update.CommandText = "UPDATE Global SET currentWordNumber = " + WordNumber.ToString();
            Update.ExecuteNonQuery();
        }

        /// <summary>
        /// 查询当前单词表当前进度
        /// </summary>
        public List<int> SelectCount()
        {
            BookCount Temp = new BookCount();
            CountList = DataBase.Query<BookCount>($"select * from Count where bookName = '{TABLE_NAME}'", Temp);
            var CountArray = CountList.ToArray();
            List<int> Output = new List<int>();
            if (CountArray.Length == 0)
            {
                Output.Add(0);
                Output.Add(0);
                return Output;
            }
            Output.Add(CountArray[0].current);
            Output.Add(CountArray[0].number);
            return Output;
            // }
            // }
            // return Output;
        }
        #endregion

        #region 英语部分
        /// <summary>
        /// 查找某本书的所有单词
        /// </summary>
        public void SelectWordList()
        {
            // 清空上次学习的卡片列表，防止多次调用积累重复卡片
            NewCardLst.Clear();
            LearningCardLst.Clear();
            ReviewedCardLst.Clear();
            AllWordList = null;
            AllJpWordList = null;

            // "自定义"不是真实表名，回退到 GRE_2 表
            // 注意：必须同步更新 TABLE_NAME 静态字段，否则后续 SELECT 会失败
            if (TABLE_NAME.IndexOf("自定义") != -1)
                TABLE_NAME = "GRE_2";

            String cmdtext = $"PRAGMA table_info({TABLE_NAME})";
            SQLiteCommand Update = DataBase.CreateCommand();
            Update.CommandText = cmdtext;
            var dr = Update.ExecuteReader();
            List<string> HeadTileList = new List<string>();
            while (dr.Read())//loop through the various columns and their info
            {
                string name = (string)dr.GetValue(1);//0:cid;1:name; 2:type;3:notnull;4:dflt_value;5:pk
                HeadTileList.Add(name);
                //Console.WriteLine(name);
            }
            dr.Close();
            if (HeadTileList.Contains("difficulty") == false)
            {
                Update.CommandText = $"ALTER TABLE {TABLE_NAME} ADD COLUMN difficulty REAL NOT NULL DEFAULT {Parameters.diffcultyDefaultValue}";
                Update.ExecuteNonQuery();
            }
            if (HeadTileList.Contains("daysBetweenReviews") == false)
            {
                Update.CommandText = $"ALTER TABLE {TABLE_NAME} ADD COLUMN daysBetweenReviews  REAL NOT NULL DEFAULT {Parameters.daysBetweenReviewsDefaultValue}";
                Update.ExecuteNonQuery();
            }
            if (HeadTileList.Contains("lastScore") == false)
            {
                Update.CommandText = $"ALTER TABLE {TABLE_NAME} ADD COLUMN lastScore REAL NOT NULL DEFAULT 0";
                Update.ExecuteNonQuery();
            }
            if (HeadTileList.Contains("dateLastReviewed") == false)
            {
                Update.CommandText = $"ALTER TABLE {TABLE_NAME} ADD COLUMN dateLastReviewed TEXT  DEFAULT NULL";
                Update.ExecuteNonQuery();
            }
            if (HeadTileList.Contains("dateLearingDue") == false)
            {
                Update.CommandText = $"ALTER TABLE {TABLE_NAME} ADD COLUMN dateLearingDue TEXT  DEFAULT NULL";
                Update.ExecuteNonQuery();
            }
            Word Temp = new Word();
            AllWordList = DataBase.Query<Word>("select * from " + TABLE_NAME, Temp);

            foreach (var Word in AllWordList)
            {
                Card cardi = new Card(Word);
                if (cardi.status == Cardstatus.New)
                    NewCardLst.Add(cardi);
                else if (cardi.status == Cardstatus.Reviewed)
                    ReviewedCardLst.Add(cardi);
                else
                    LearningCardLst.Add(cardi);
            }
        }

        public void updateCardDateBase(List<Card> cardList)
        {
            SQLiteCommand Update = DataBase.CreateCommand();

            foreach (var card in cardList)
            {
                try
                {
                    // Step1/Step2 词未进入 Reviewed，dateLastReviewed 为 default(0001-01-01) 时写 NULL，避免写入无效日期
                    string dlr = (card.dateLastReviewed == default(DateTime) || card.dateLastReviewed.Year <= 1)
                        ? "NULL"
                        : "'" + card.dateLastReviewed.ToString("yyyy/M/d H:m:s") + "'";
                    // 学习中的词（Step1/Step2/Relearn）下次到期时间；未设置(0001-01-01)写 NULL
                    string dld = (card.dateLearingDue == default(DateTime) || card.dateLearingDue.Year <= 1)
                        ? "NULL"
                        : "'" + card.dateLearingDue.ToString("yyyy/M/d H:m:s") + "'";
                    String Command = $"UPDATE {TABLE_NAME} SET status = {(int)card.status}, " +
                        $"difficulty ={card.difficulty}, daysBetweenReviews ={card.daysBetweenReviews}, " +
                        $"lastScore ={card.lastScore}, dateLastReviewed ={dlr}, dateLearingDue ={dld} " +
                        $"WHERE wordRank = {card.word.wordRank};";
                    Update.CommandText = Command;
                    Update.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"updateCardDateBase error for word {card.word.headWord}: {ex.Message}");
                }
            }

        }

        public void GetOverdueReviewedCardList(int maxReviewedCardNumer, out List<Card> usedReviewedCardLst)
        {
            usedReviewedCardLst = new List<Card>();

            // 复习池 = 逾期词优先 + 未到期词补足（2026-09-16 修复）
            // 逾期词（percentOverdue >= 1）全部入池；若不足 maxReviewedCardNumer，
            // 按「最接近到期」降序用未到期的词补足。
            // 此前只取逾期词，导致没有逾期词时复习队列恒为空（详见 CLAUDE.md #64）。
            var reviewPool = ReviewedCardLst.Where(c => c.percentOverdue >= 1.0).ToList();

            if (reviewPool.Count < maxReviewedCardNumer)
            {
                reviewPool.AddRange(ReviewedCardLst
                    .Where(c => c.percentOverdue < 1.0)
                    .OrderByDescending(c => c.percentOverdue)
                    .Take(maxReviewedCardNumer - reviewPool.Count));
            }

            if (reviewPool.Count < maxReviewedCardNumer)
                maxReviewedCardNumer = reviewPool.Count;

            // 加权随机选择：SM2+ 得分越低（越不熟）、逾期越长（越该复习），权重越高
            // 公式: weight = (1.0 + percentOverdue) * (1.5 - lastScore)
            // Again(0.4): 高权重  Hard(0.6): 中高  Good(0.8): 中  Easy(1.0): 低
            Random rnd = _random;
            List<double> weights = new List<double>();
            double totalWeight = 0;

            foreach (var card in reviewPool)
            {
                double overdueWeight = 1.0 + card.percentOverdue;
                double scoreWeight = 1.5 - card.lastScore;
                if (scoreWeight < 0.1) scoreWeight = 0.1; // 至少有一点权重
                double w = overdueWeight * scoreWeight;
                weights.Add(w);
                totalWeight += w;
            }

            // 加权不放回抽样
            var remainingCards = new List<Card>(reviewPool);
            var remainingWeights = new List<double>(weights);

            for (int i = 0; i < maxReviewedCardNumer; i++)
            {
                if (remainingCards.Count == 0) break;

                double remainingTotal = 0;
                foreach (double w in remainingWeights) remainingTotal += w;

                double roll = rnd.NextDouble() * remainingTotal;
                double cumulative = 0;
                int pickIdx = remainingCards.Count - 1;

                for (int j = 0; j < remainingCards.Count; j++)
                {
                    cumulative += remainingWeights[j];
                    if (roll <= cumulative)
                    {
                        pickIdx = j;
                        break;
                    }
                }

                usedReviewedCardLst.Add(remainingCards[pickIdx]);
                remainingCards.RemoveAt(pickIdx);
                remainingWeights.RemoveAt(pickIdx);
            }
        }

        public void GenerateRandomNewCardList(int maxNewCardNumber, out List<Card> usedNewCardLst)
        {
            //SelectWordList();

            //List<Card>
            usedNewCardLst = new List<Card>();

            if (NewCardLst.Count < maxNewCardNumber)
                maxNewCardNumber = NewCardLst.Count;

            Random Rd = _random;
            for (int i = 0; i < maxNewCardNumber; i++)
            {
                int Index = Rd.Next(NewCardLst.Count);
                usedNewCardLst.Add(NewCardLst[Index]);
                NewCardLst.RemoveAt(Index);
            }
        }


        /// <summary>
        /// 从词库里随机选择Number个单词
        /// </summary>
        /// <typeparam name="List<Word>"></typeparam>
        /// <param name="Number"></param>
        /// <returns></returns>
        public List<Word> GetRandomWordList(int Number)
        {
            List<Word> Result = new List<Word>();
            //SelectWordList();
            //var AllWordArray = AllWordList.ToList();



            //把所有没背过的单词序号都存在WordList里了
            List<Word> WordList = new List<Word>();
            foreach (var Word in AllWordList)
            {
                if (Word.status == 0) //单词是否背过
                {
                    WordList.Add(Word);
                }
            }

            if (WordList.Count == 0)
                return Result;
            else if (WordList.Count < Number)
                Number = WordList.Count;

            Random Rd = _random;
            for (int i = 0; i < Number; i++)
            {
                int Index = Rd.Next(WordList.Count);//下标
                Result.Add(WordList[Index]);
                WordList.RemoveAt(Index);
            }
            return Result;
        }

        /// <summary>
        /// 获取俩随机单词，作为错误答案
        /// </summary>
        public List<Word> GetRandomWords(int Number)
        {
            List<Word> Result = new List<Word>();
            var AllWordArray = AllWordList.ToList();

            Random Rd = _random;
            int actual = Math.Min(Number, AllWordArray.Count);
            for (int i = 0; i < actual; i++)
            {
                int Index = Rd.Next(AllWordArray.Count);
                Result.Add(AllWordArray[Index]);
                AllWordArray.RemoveAt(Index);
            }
            return Result;
        }

        /// <summary>
        /// 为惊喜复习获取干扰词。按同词性已学词 → 已学词 → 全部词 三级回退。
        /// 自动排除指定 wordRank 的词（避免选到目标词自身）。
        /// </summary>
        /// <param name="count">需要的词数</param>
        /// <param name="excludeWordRank">排除的词 rank</param>
        /// <param name="targetPos">目标词的词性，用于优先匹配同词性词。可为空</param>
        public List<Word> GetRandomReviewableWords(int count, int excludeWordRank, string targetPos)
        {
            var targetPosParts = ParsePosParts(targetPos);
            var allWords = AllWordList.ToList();

            // 三级筛选池
            var pool1 = new List<Word>(); // POS 匹配的已学词
            var pool2 = new List<Word>(); // 已学词（不限 POS）
            var pool3 = new List<Word>(); // 全部词（排除自身）

            foreach (var w in allWords)
            {
                if (w.wordRank == excludeWordRank) continue;
                if (w.status != 0)
                {
                    if (targetPosParts.Count > 0)
                    {
                        var candParts = ParsePosParts(w.pos);
                        if (candParts.Overlaps(targetPosParts))
                            pool1.Add(w);
                        else
                            pool2.Add(w);
                    }
                    else
                    {
                        pool2.Add(w);
                    }
                }
                else
                {
                    pool3.Add(w);
                }
            }

            // 按优先级选择：pool1 → pool1+pool2 → pool1+pool2+pool3
            List<Word> source;
            if (pool1.Count >= count)
                source = pool1;
            else if (pool1.Count + pool2.Count >= count)
                { pool1.AddRange(pool2); source = pool1; }
            else
                { pool1.AddRange(pool2); pool1.AddRange(pool3); source = pool1; }

            var result = new List<Word>();
            int actual = Math.Min(count, source.Count);
            for (int i = 0; i < actual; i++)
            {
                int idx = _random.Next(source.Count);
                result.Add(source[idx]);
                source.RemoveAt(idx);
            }
            return result;
        }

        /// <summary>
        /// 解析 POS 字符串为标准化词性标记集合。
        /// 处理格式："n / vt"→{"n","vt"}  "adj."→{"adj"}  "n&v"→{"n","v"}
        /// </summary>
        private static HashSet<string> ParsePosParts(string pos)
        {
            var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(pos)) return parts;
            foreach (var seg in pos.Split('/'))
            {
                foreach (var sub in seg.Split('&'))
                {
                    var p = sub.Trim().TrimEnd('.');
                    if (p.Length > 0) parts.Add(p);
                }
            }
            return parts;
        }
        #endregion

        #region 日语部分
        /// <summary>
        /// 查找某本书的所有单词
        /// </summary>
        public void SelectJpWordList()
        {
            JpWord Temp = new JpWord();
            AllJpWordList = DataBase.Query<JpWord>("select * from " + TABLE_NAME, Temp);
        }

        /// <summary>
        /// 从词库里随机选择Number个单词
        /// </summary>
        /// <typeparam name="List<Word>"></typeparam>
        /// <param name="Number"></param>
        /// <returns></returns>
        public List<JpWord> GetRandomJpWordList(int Number)
        {
            List<JpWord> Result = new List<JpWord>();
            SelectJpWordList();
            var AllWordArray = AllJpWordList.ToList();

            // 存储未学词在整个列表中的索引
            List<int> WordList = new List<int>();
            for (int idx = 0; idx < AllWordArray.Count; idx++)
            {
                if (AllWordArray[idx].status == 0) //单词是否背过
                {
                    WordList.Add(idx);
                }
            }

            if (WordList.Count == 0)
                return Result;
            else if (WordList.Count < Number)
                Number = WordList.Count;

            Random Rd = _random;
            for (int i = 0; i < Number; i++)
            {
                int pick = Rd.Next(WordList.Count);
                int realIdx = WordList[pick];
                Result.Add(AllWordArray[realIdx]);
                WordList.RemoveAt(pick);
                // 更新后续索引（RemoveAt 之后所有大于 realIdx 的索引需递减）
                for (int j = 0; j < WordList.Count; j++)
                {
                    if (WordList[j] > realIdx)
                        WordList[j]--;
                }
            }
            return Result;
        }

        /// <summary>
        /// 获取俩随机单词，作为错误答案
        /// </summary>
        public List<JpWord> GetRandomJpWords(int Number)
        {
            List<JpWord> Result = new List<JpWord>();
            SelectJpWordList();
            var AllWordArray = AllJpWordList.ToList();

            if (AllWordArray.Count == 0)
                return Result;

            Random Rd = _random;
            int actual = Math.Min(Number, AllWordArray.Count);
            for (int i = 0; i < actual; i++)
            {
                int Index = Rd.Next(AllWordArray.Count);
                Result.Add(AllWordArray[Index]);
                AllWordArray.RemoveAt(Index);
            }
            return Result;
        }
        #endregion

        #region 五十音部分
        public List<GoinWord> GetGainWordList()
        {
            GoinWord Temp = new GoinWord();
            IEnumerable<GoinWord> AllGoinWordList = DataBase.Query<GoinWord>("select * from " + TABLE_NAME, Temp);
            return AllGoinWordList.ToList();
        }

        public int GetGoinProgress()
        {
            BookCount Temp = new BookCount();
            CountList = DataBase.Query<BookCount>("select * from Count where bookName = 'Goin'", Temp);
            var CountArray = CountList.ToList();
            if (CountArray.Count == 0)
                return 0;
            return CountArray[0].current;
        }

        public List<GoinWord> GetTwoGoinRandomWords(GoinWord CurrentWord)
        {
            List<GoinWord> Result = new List<GoinWord>();
            List<GoinWord> WordList = GetGainWordList();

            if (WordList.Count <= 1)
            {
                Result.Add(CurrentWord);
                return Result;
            }

            Random Rd = _random;
            for (int i = 0; i < 2; i++)
            {
                int Index = Rd.Next(WordList.Count);
                if (CurrentWord.wordRank == Index + 1)
                {
                    i--;
                    continue;
                }
                Result.Add(WordList[Index]);
                WordList.RemoveAt(Index);
            }
            return Result;
        }
        #endregion

        #region SM2+ 自动修复与备份
        private static DateTime _fixLastRun = DateTime.MinValue;
        private static DateTime _preBackupTime = DateTime.MinValue;

        // 英语词库列表
        private static readonly string[] EnglishTables = {
            "CET4_1","CET4_3","CET6_1","CET6_2","CET6_3",
            "Level4_1","Level4luan_2","Level8_1","Level8luan_2",
            "KaoYan_1","KaoYan_2","KaoYan_3","IELTS_3","TOEFL_2",
            "GRE_2","GMAT_3","SAT_2"
        };

        /// <summary>
        /// 首次启动时自动修复 SM2+ 参数
        /// 检测标记文件，只运行一次
        /// </summary>
        /// <summary>
        /// 读取「待生成 AI 短文的新词」wordRank 列表（2026-09-16 新增，见 CLAUDE.md #67）。
        /// 存于 Global.pendingEssayWords（JSON 数组），用于「累计 N 个新词才生成一次短文」的门控。
        /// 使用独立连接，不受调用方（学习线程）事务影响。
        /// </summary>
        public static List<int> LoadPendingEssayWords()
        {
            var list = new List<int>();
            try
            {
                using (SQLiteConnection db = new SQLiteConnection(
                    @"Data Source=" + System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) +
                    @"\Resources\inami.db;Version=3"))
                {
                    db.Open();
                    SQLiteCommand cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT pendingEssayWords FROM Global LIMIT 1";
                    object v = cmd.ExecuteScalar();
                    string json = (v == null || v == DBNull.Value) ? "" : v.ToString();
                    if (!string.IsNullOrEmpty(json))
                    {
                        var jss = new System.Web.Script.Serialization.JavaScriptSerializer();
                        var arr = jss.Deserialize<List<int>>(json);
                        if (arr != null) list = arr;
                    }
                }
            }
            catch { /* 列缺失或 JSON 损坏时按空处理 */ }
            return list;
        }

        /// <summary>
        /// 写入待生成短文的 wordRank 列表（2026-09-16）。
        /// 测试模式下跳过，避免污染（与 SaveEssayLog 同策略）。
        /// </summary>
        public static void SavePendingEssayWords(List<int> ranks)
        {
            if (ToastFish.Model.PushControl.PushWords.TestMode) return;
            try
            {
                var jss = new System.Web.Script.Serialization.JavaScriptSerializer();
                string json = jss.Serialize(ranks ?? new List<int>());
                using (SQLiteConnection db = new SQLiteConnection(
                    @"Data Source=" + System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) +
                    @"\Resources\inami.db;Version=3"))
                {
                    db.Open();
                    SQLiteCommand cmd = db.CreateCommand();
                    cmd.CommandText = "UPDATE Global SET pendingEssayWords = @j";
                    cmd.Parameters.AddWithValue("@j", json);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        /// <summary>清空待生成短文的累积列表（生成成功后调用，2026-09-16）。</summary>
        public static void ClearPendingEssayWords()
        {
            SavePendingEssayWords(new List<int>());
        }

        public static void AutoCheckAndFix()
        {
            // 每天最多自动修复一次
            if ((DateTime.Now - _fixLastRun).TotalHours < 24)
                return;
            _fixLastRun = DateTime.Now;

            // 只修复超过该天数未复习的词（避免误伤刚学的新词，见 while 循环内说明）
            const int STALE_DAYS = 30;
            int totalFixed = 0;
            try
            {
                using (SQLiteConnection db = new SQLiteConnection(
                    @"Data Source=" + System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) +
                    @"\Resources\inami.db;Version=3"))
                {
                    db.Open();
                foreach (string table in EnglishTables)
                {
                    try
                    {
                        // 添加 SM2+ 列（如果缺失）
                        string[] newCols = {"difficulty","daysBetweenReviews","lastScore","dateLastReviewed","dateLastReviewed_bak"};
                        foreach (string col in newCols)
                        {
                            try
                            {
                                SQLiteCommand addCol = db.CreateCommand();
                                string defaultVal = col == "difficulty" ? "0.3" :
                                                   col == "daysBetweenReviews" ? "3.0" :
                                                   col == "lastScore" ? "0" :
                                                   col == "dateLastReviewed_bak" ? "NULL" : "NULL";
                                string colType = col.StartsWith("date") ? "TEXT" : "REAL";
                                addCol.CommandText = $"ALTER TABLE [{table}] ADD COLUMN {col} {colType} NOT NULL DEFAULT {defaultVal}";
                                addCol.ExecuteNonQuery();
                            }
                            catch { /* 列已存在，忽略 */ }
                        }

                        // 备份已有复习记录
                        SQLiteCommand backup = db.CreateCommand();
                        backup.CommandText = $"UPDATE [{table}] SET dateLastReviewed_bak = dateLastReviewed WHERE status = 5 AND dateLastReviewed IS NOT NULL AND dateLastReviewed != '' AND dateLastReviewed_bak IS NULL";
                        backup.ExecuteNonQuery();

                        // 对已学但未修复的词应用 SM2+
                        SQLiteCommand findCmd = db.CreateCommand();
                        findCmd.CommandText = $"SELECT wordRank, difficulty, daysBetweenReviews, lastScore, dateLastReviewed FROM [{table}] WHERE status = 5";
                        SQLiteDataReader reader = findCmd.ExecuteReader();
                        while (reader.Read())
                        {
                            double diff = reader.IsDBNull(1) ? 0.3 : Convert.ToDouble(reader[1]);
                            double interval = reader.IsDBNull(2) ? 3.0 : Convert.ToDouble(reader[2]);
                            double score = reader.IsDBNull(3) ? 0 : Convert.ToDouble(reader[3]);
                            string dlr = reader.IsDBNull(4) ? "" : reader.GetString(4);

                            // 只修复还是默认值的词
                            if (Math.Abs(diff - 0.3) > 0.001 || Math.Abs(interval - 3.0) > 0.01)
                                continue;

                            int rank = reader.GetInt32(0);
                            DateTime oldDate = DateTime.Now.AddDays(-3);
                            if (!string.IsNullOrEmpty(dlr))
                            {
                                try { oldDate = DateTime.ParseExact(dlr.Substring(0, 19), "yyyy/M/d H:m:s", null); }
                                catch { try { oldDate = DateTime.ParseExact(dlr.Substring(0, 19), "yyyy-MM-dd H:m:s", null); } catch { } }
                            }

                            // 【2026-09-16】只修复真正「陈旧」的词。
                            // 新词首次答「牢记」直接进 Reviewed 时，Card.updateCard 算得 daysSpan=0
                            // → podue=0，故 diff/interval 会**合法地**保持初始值 0.3/3.0，被上面的
                            // 「是否默认值」判断误判为「未修复」，于是每次启动都刷新其
                            // dateLastReviewed，使复习被无限推迟（详见 CLAUDE.md #64）。
                            if ((DateTime.Now - oldDate).TotalDays < STALE_DAYS)
                                continue;

                            double elapsed = Math.Max(0, (DateTime.Now - oldDate).TotalDays);
                            ApplySM2(ref diff, ref interval, score, elapsed);

                            // 同时更新 dateLastReviewed 为当前时间，避免 Card.updateCard()
                            // 再次叠加 elapsed 导致 difficulty→0、interval→70+（双重补偿）
                            SQLiteCommand fixCmd = db.CreateCommand();
                            fixCmd.CommandText = $"UPDATE [{table}] SET difficulty={diff:F4}, daysBetweenReviews={interval:F2}, dateLastReviewed='{DateTime.Now:yyyy/M/d H:m:s}' WHERE wordRank={rank}";
                            fixCmd.ExecuteNonQuery();
                            totalFixed++;
                        }
                        reader.Close();
                    }
                    catch { /* 表可能不存在 */ }
                }

                // 更新 Count 的 current（已学数）和 number（总词数）
                foreach (string table in EnglishTables)
                {
                    try
                    {
                        SQLiteCommand cnt = db.CreateCommand();
                        cnt.CommandText = $"UPDATE Count SET current = (SELECT COUNT(*) FROM [{table}] WHERE status != 0), number = (SELECT COUNT(*) FROM [{table}]) WHERE bookName = '{table}'";
                        cnt.ExecuteNonQuery();
                    }
                    catch { }
                }

                // 【2026-09-16 已删除】原「一次性迁移」段：
                //   UPDATE [{table}] SET dateLastReviewed = @now, dateLastReviewed_bak = @now
                //   WHERE status = 5 AND (diff/interval 已非默认值) AND dateLastReviewed < @weekAgo
                // 该段用**文本比较**判断日期，而 @weekAgo 格式为 "yyyy/M/d"（无前导零），
                // 导致 '2026/9/15 ...' < '2026/9/9' 成立（'1' < '9'）——9 号以后学的词
                // 每次启动都被误判为「超过 7 天未复习」并刷新时间戳，使 percentOverdue
                // 恒为 0、复习队列恒为空（一次性误刷 1193 词）。该迁移早已完成使命，直接移除。
                // 严禁再引入文本日期比较，需要比较日期请用 julianday()。

                }  // end using (db)

                // 写入标记文件，提示已修复
                string markerPath = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) + @"\Resources\.sm2_fixed";
                if (totalFixed > 0)
                {
                    System.IO.File.WriteAllText(markerPath,
                        $"修复于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，共 {totalFixed} 词。\n以后每次学习自动备份和应用。");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SM2+ 自动修复异常: " + ex.Message);
            }
        }

        public static void PreLearnBackup()
        {
            _preBackupTime = DateTime.Now;
            try
            {
                using (SQLiteConnection db = new SQLiteConnection(
                    @"Data Source=" + System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) +
                    @"\Resources\inami.db;Version=3"))
                {
                    db.Open();

                    foreach (string table in EnglishTables)
                    {
                        try
                        {
                            SQLiteCommand cmd = db.CreateCommand();
                            cmd.CommandText = $"UPDATE [{table}] SET dateLastReviewed_bak = dateLastReviewed WHERE dateLastReviewed IS NOT NULL AND dateLastReviewed != ''";
                            cmd.ExecuteNonQuery();
                        }
                        catch { }
                    }
                }  // end using
            }
            catch { }
        }

        /// <summary>
        /// 自动数据库快照（2026-08-08 防污染机制）：每次启动把 inami.db 备份到
        /// DbSnapshots\inami.db.auto_时间戳（SQLite backup API，可靠处理 WAL），
        /// 自动保留最近 5 份，供任意时刻回滚。
        /// 必须在 AutoCheckAndFix 等写入操作之前调用，确保快照是「写入前」状态。
        /// </summary>
        public static void AutoSnapshotDb()
        {
            try
            {
                string exeDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string dbPath = System.IO.Path.Combine(exeDir, "Resources", "inami.db");
                if (!System.IO.File.Exists(dbPath)) return;

                string snapDir = System.IO.Path.Combine(exeDir, "DbSnapshots");
                System.IO.Directory.CreateDirectory(snapDir);
                string snapPath = System.IO.Path.Combine(snapDir,
                    "inami.db.auto_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

                using (SQLiteConnection src = new SQLiteConnection(@"Data Source=" + dbPath + ";Version=3"))
                using (SQLiteConnection dst = new SQLiteConnection(@"Data Source=" + snapPath + ";Version=3"))
                {
                    src.Open();
                    dst.Open();
                    src.BackupDatabase(dst, "main", "main", -1, null, 0);
                }

                // 清理旧快照，保留最近 5 份
                var snaps = System.IO.Directory.GetFiles(snapDir, "inami.db.auto_*")
                    .OrderByDescending(f => f).ToList();
                for (int i = 5; i < snaps.Count; i++)
                {
                    try { System.IO.File.Delete(snaps[i]); } catch { }
                }
            }
            catch { }
        }

        public static PostLearnResult PostLearnApply()
        {
            var result = new PostLearnResult();

            try
            {
                using (SQLiteConnection db = new SQLiteConnection(
                    @"Data Source=" + System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) +
                    @"\Resources\inami.db;Version=3"))
                {
                    db.Open();

                    foreach (string table in EnglishTables)
                {
                    try
                    {
                        SQLiteCommand findCmd = db.CreateCommand();
                        findCmd.CommandText = $"SELECT wordRank, lastScore, dateLastReviewed, dateLastReviewed_bak FROM [{table}] WHERE status = 5 AND lastScore > 0 AND dateLastReviewed IS NOT NULL AND dateLastReviewed != '' AND (dateLastReviewed_bak IS NULL OR dateLastReviewed_bak = '' OR dateLastReviewed != dateLastReviewed_bak)";
                        SQLiteDataReader reader = findCmd.ExecuteReader();
                        while (reader.Read())
                        {
                            double score = reader.IsDBNull(1) ? 0 : Convert.ToDouble(reader[1]);
                            int rank = reader.GetInt32(0);
                            string newDlr = reader.IsDBNull(2) ? "" : reader.GetString(2);

                            if (score >= 0.95) result.EasyCount++;
                            else if (score >= 0.75) result.GoodCount++;
                            else if (score >= 0.55) result.HardCount++;
                            else if (score > 0) result.AgainCount++;
                            result.TotalWords++;

                            // 更新 bak 为当前值，标记已统计
                            SQLiteCommand fixCmd = db.CreateCommand();
                            fixCmd.CommandText = $"UPDATE [{table}] SET dateLastReviewed_bak = @dlr WHERE wordRank = @rank";
                            fixCmd.Parameters.AddWithValue("@dlr", newDlr);
                            fixCmd.Parameters.AddWithValue("@rank", rank);
                            fixCmd.ExecuteNonQuery();
                        }
                        reader.Close();
                    }
                    catch { }
                }

                // 更新每张表的 Count.current（已学数）和 number（总词数）
                foreach (string table in EnglishTables)
                {
                    try
                    {
                        SQLiteCommand cntCmd = db.CreateCommand();
                        cntCmd.CommandText = $"UPDATE Count SET current = (SELECT COUNT(*) FROM [{table}] WHERE status != 0), number = (SELECT COUNT(*) FROM [{table}]) WHERE bookName = '{table}'";
                        cntCmd.ExecuteNonQuery();
                    }
                    catch { }
                }

                }  // end using (db)
            }
            catch { }

            return result;
        }

        /// <summary>
        /// 学习后统计结果
        /// </summary>
        public class PostLearnResult
        {
            public int TotalWords { get; set; }
            public int EasyCount { get; set; }
            public int GoodCount { get; set; }
            public int HardCount { get; set; }
            public int AgainCount { get; set; }
        }

        private static void ApplySM2(ref double difficulty, ref double daysBetweenReviews, double lastScore, double actualElapsedDays)
        {
            bool correct = lastScore >= 0.7;
            double podue;
            if (daysBetweenReviews > 0.01)
                podue = correct ? Math.Min(2.0, actualElapsedDays / daysBetweenReviews) : 1.0;
            else
                podue = correct ? 0 : 1.0;

            difficulty += podue * (8 - 10 * lastScore) / 17;
            if (difficulty < 0) difficulty = 0;
            if (difficulty > 1) difficulty = 1;

            double dfweight = 3 - 1.7 * difficulty;
            Random rnd = _random;
            if (correct)
                daysBetweenReviews *= (1 + (dfweight - 1) * podue * (0.95 + 0.1 * rnd.NextDouble()));
            else
                daysBetweenReviews /= (1 + 3 * difficulty);

            if (daysBetweenReviews < 1) daysBetweenReviews = 1;
            if (daysBetweenReviews > 365) daysBetweenReviews = 365;
        }

        /// <summary>创建 EssayLog 表（2026-07-22），存储每次 AI 生成的短文。</summary>
        public static void CreateEssayLogTable()
        {
            try
            {
                using (SQLiteConnection db = new SQLiteConnection(
                    @"Data Source=" + System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) +
                    @"\Resources\inami.db;Version=3"))
                {
                    db.Open();
                    string sql = @"CREATE TABLE IF NOT EXISTS EssayLog (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        createdAt   TEXT NOT NULL,
                        bookName    TEXT NOT NULL,
                        wordCount   INTEGER NOT NULL,
                        words       TEXT NOT NULL,
                        essayEN     TEXT NOT NULL,
                        essayCN     TEXT,
                        questions   TEXT,
                        attempted   INTEGER DEFAULT 0,
                        correct     INTEGER DEFAULT 0,
                        modelMode   INTEGER DEFAULT 0
                    )";
                    using (SQLiteCommand cmd = new SQLiteCommand(sql, db))
                        cmd.ExecuteNonQuery();
                }
            }
            catch { /* 表已存在或数据库不可用 */ }
        }

        /// <summary>写入 AI 短文记录（2026-07-22）。</summary>
        public static void InsertEssayLog(string createdAt, string bookName, int wordCount,
            string wordsJson, string essayEN, string essayCN, string questionsJson,
            int attempted, int correct, int modelMode)
        {
            try
            {
                using (SQLiteConnection db = new SQLiteConnection(
                    @"Data Source=" + System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) +
                    @"\Resources\inami.db;Version=3"))
                {
                    db.Open();
                    string sql = @"INSERT INTO EssayLog
                        (createdAt,bookName,wordCount,words,essayEN,essayCN,questions,attempted,correct,modelMode)
                        VALUES (@c,@b,@wc,@w,@en,@cn,@q,@a,@cr,@mm)";
                    using (SQLiteCommand cmd = new SQLiteCommand(sql, db))
                    {
                        cmd.Parameters.AddWithValue("@c", createdAt);
                        cmd.Parameters.AddWithValue("@b", bookName);
                        cmd.Parameters.AddWithValue("@wc", wordCount);
                        cmd.Parameters.AddWithValue("@w", wordsJson);
                        cmd.Parameters.AddWithValue("@en", essayEN);
                        cmd.Parameters.AddWithValue("@cn", essayCN ?? "");
                        cmd.Parameters.AddWithValue("@q", questionsJson ?? "[]");
                        cmd.Parameters.AddWithValue("@a", attempted);
                        cmd.Parameters.AddWithValue("@cr", correct);
                        cmd.Parameters.AddWithValue("@mm", modelMode);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { /* 静默 */ }
        }
        #endregion
    }

    #region 查询类
    [Serializable]
    public class Word
    {
        public int wordRank { get; set; }
        public int status { get; set; }
        public String headWord { get; set; }
        public String usPhone { get; set; }
        public String ukPhone { get; set; }
        public String usSpeech { get; set; }
        public String ukSpeech { get; set; }
        public String tranCN { get; set; }
        public String pos { get; set; }
        public String tranOther { get; set; }
        public String question { get; set; }
        public String explain { get; set; }
        public String rightIndex { get; set; }
        public String examType { get; set; }
        public String choiceIndexOne { get; set; }
        public String choiceIndexTwo { get; set; }
        public String choiceIndexThree { get; set; }
        public String choiceIndexFour { get; set; }
        public String sentence { get; set; }
        public String sentenceCN { get; set; }
        public String phrase { get; set; }
        public String phraseCN { get; set; }
        public double difficulty { get; set; }
        public double daysBetweenReviews { get; set; }
        public double lastScore { get; set; }
        public String dateLastReviewed { get; set; }
        public String dateLearingDue { get; set; }
    }

    [Serializable]
    public class BookCount
    {
        public String bookName { get; set; }
        public int number { get; set; }
        public int current { get; set; }
    }

    [Serializable]
    public class GoinWord
    {
        public int wordRank { get; set; }
        public string bookId { get; set; }
        public int status { get; set; }
        public string romaji { get; set; }
        public string hiragana { get; set; }
        public string katakana { get; set; }

    }

    [Serializable]
    public class Global
    {
        public string currentWordNumber { get; set; }
        public string currentBookName { get; set; }
        public int autoPlay { get; set; }
        public int EngType { get; set; }
        public int autoLog { get; set; }
        /// <summary>待生成 AI 短文的新词 wordRank（JSON 数组，2026-09-16 新增，见 CLAUDE.md #67）。</summary>
        public string pendingEssayWords { get; set; }
    }

    [Serializable]
    public class JpWord
    {
        public int wordRank { get; set; }
        public string bookId { get; set; }
        public int status { get; set; }
        public String headWord { get; set; }
        public int Phone { get; set; }
        public String tranCN { get; set; }
        public String pos { get; set; }
        public String hiragana { get; set; }
    }

    [Serializable]
    public class CustomizeWord
    {
        public String firstLine { get; set; }
        public String secondLine { get; set; }
        public String thirdLine { get; set; }
        public String fourthLine { get; set; }
    }
    #endregion
}
