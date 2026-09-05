using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace ToastFish.Model.Ai
{
    /// <summary>
    /// AI 短文配置。存储于 Resources\ai_config.txt（key=value 格式，明文）。
    /// readingMode: 0=例句串读（默认）, 1=AI短文
    /// modelMode: 0=快速（flash，秒级）, 1=推理（pro 深度思考，约1~2分钟，质量更高）
    /// </summary>
    public static class AiConfig
    {
        public static int ReadingMode = 0;
        public static int ModelMode = 0;
        public static string ApiKey = "";
        public static string BaseUrl = "https://api.deepseek.com";
        public static string Model = "deepseek-v4-flash";
        public static string ReasonModel = "deepseek-v4-pro";

        private static bool _loaded = false;

        private static string ConfigPath
        {
            get
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(exeDir, "Resources", "ai_config.txt");
            }
        }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(ConfigPath)) return;
                foreach (string line in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "readingMode": int.TryParse(v, out ReadingMode); break;
                        case "modelMode": int.TryParse(v, out ModelMode); break;
                        case "apiKey": ApiKey = v; break;
                        case "baseUrl": if (v != "") BaseUrl = v; break;
                        case "model": if (v != "") Model = v; break;
                        case "reasonModel": if (v != "") ReasonModel = v; break;
                    }
                }
            }
            catch { /* 配置损坏时使用默认值 */ }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("readingMode=" + ReadingMode);
                sb.AppendLine("modelMode=" + ModelMode);
                sb.AppendLine("apiKey=" + ApiKey);
                sb.AppendLine("baseUrl=" + BaseUrl);
                sb.AppendLine("model=" + Model);
                sb.AppendLine("reasonModel=" + ReasonModel);
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>AI 阅读理解题</summary>
    public class EssayQuestion
    {
        public string Question;
        public List<string> Choices = new List<string>();
        public int Answer = -1;   // 正确选项下标
    }

    /// <summary>AI 短文生成结果</summary>
    public class EssayResult
    {
        public string EssayEN;
        public string EssayCN;
        public List<EssayQuestion> Questions = new List<EssayQuestion>();
    }

    /// <summary>15选10 完形填空候选词</summary>
    public class ClozeCandidate
    {
        public string Word;   // 候选词（已变形，如 adopted 而非 adopt）
        public int Blank = -1; // 正确词对应的空位下标(0~9)；干扰词=-1
    }

    /// <summary>15选10 完形填空 AI 生成结果</summary>
    public class ClozeResult
    {
        public string Text;     // 短文，含 [0]...[9] 挖空标记
        public string TextCN;   // 中文翻译
        public List<ClozeCandidate> Candidates = new List<ClozeCandidate>(); // 15个候选
    }

    /// <summary>
    /// DeepSeek（OpenAI 兼容格式）短文生成。
    /// 同步调用（学习后台线程），失败返回 false 不抛异常，调用方回退例句串读。
    /// </summary>
    public static class EssayGenerator
    {
        private const int TIMEOUT_FAST_MS = 30000;     // 快速模式（flash，秒级生成）
        private const int TIMEOUT_REASON_MS = 600000;  // 推理模式（pro 深度思考，10 分钟；生成"不限时"，180 秒由上层主动中止兜底）

        // ===== 多轮自检生成（2026-08-08 方案 C）：快速模式生成后最多追加 MAX_REVISE_ROUNDS 次修正轮 =====
        private const int MAX_REVISE_ROUNDS = 1;   // 快速模式最多修正轮数（共 2 次调用）；推理模式保持单轮

        /// <summary>
        /// 根据词库表名返回中文难度标签，用于动态生成 AI 短文 prompt（2026-09-05）。
        /// 让短文/题目难度随当前学习的词库变化，而非固定为六级。
        /// </summary>
        public static string GetBookLevel(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return "大学英语六级";
            if (tableName.StartsWith("CET4")) return "大学英语四级";
            if (tableName.StartsWith("CET6")) return "大学英语六级";
            if (tableName.StartsWith("KaoYan")) return "考研英语";
            if (tableName.StartsWith("GRE")) return "GRE";
            if (tableName.StartsWith("GMAT")) return "GMAT";
            if (tableName.StartsWith("IELTS")) return "雅思";
            if (tableName.StartsWith("TOEFL")) return "托福";
            if (tableName.StartsWith("SAT")) return "SAT";
            if (tableName.StartsWith("Level4")) return "英语专业四级";
            if (tableName.StartsWith("Level8")) return "英语专业八级";
            return "大学英语六级";
        }

        /// <summary>修正轮 system prompt（Essay 选择题版）。固定常量命中上下文缓存。模型满意回 {"ok":true} 省输出。</summary>
        private const string REVISE_PROMPT =
            "你是{LEVEL}阅读理解命题质量检查员。以下是 AI 生成的短文和 3 道题（JSON）。严格检查：" +
            "1) 正确选项是否照抄原文词句（必须同义替换/改写）；" +
            "2) 干扰项是否一眼排除（必须有一定迷惑性）；" +
            "3) 3 题题型是否互不相同（细节/推理/主旨/词义/态度）；" +
            "4) 词义猜测题的被猜词是否出现在短文中但【不在用户单词列表内】（禁止猜用户学过的词）；" +
            "5) 短文是否自然连贯、确实用上了全部给定单词；" +
            "6) 每题4个选项的长度是否严格均衡（单词数相同或仅相差1个；正确选项不得长于任何干扰项，否则视为长度泄露答案）。" +
            "若无问题，输出 {\"ok\":true}。" +
            "若存在问题，输出修正后的完整 JSON（格式与输入一致：{\"en\":\"英文短文\",\"cn\":\"中文翻译\",\"questions\":[{\"q\":\"题目\",\"choices\":[\"选项1\",\"选项2\",\"选项3\",\"选项4\"],\"answer\":0}]}）。" +
            "只输出 JSON。";

        /// <summary>修正轮 system prompt（15选10 CLOZE 版）。</summary>
        private const string REVISE_CLOZE_PROMPT =
            "你是{LEVEL}选词填空命题质量检查员。以下是 AI 生成的 15选10 完形填空（JSON）。严格检查：" +
            "1) 正文中每个空位是否被 [N] 完全替代（不得残留答案词，即禁止答案泄漏）；" +
            "2) 干扰词是否与目标空位词性一致、屈折形式一致、有真实迷惑性（不得用用户单词列表中的词作干扰词）；干扰方式是否以词性混淆/搭配不当/语义偏离为主，严格近义词干扰项是否≤1个（若大量使用近义词辨析视为命题错误，需改为搭配/语义类干扰）；" +
            "3) candidates 是否恰好 15 项（10 正确 + 5 干扰），blank 1~10 为正确、-1 为干扰；" +
            "4) 中文翻译中是否残留 [N] 占位符（必须已填入对应中文词）。" +
            "若无问题，输出 {\"ok\":true}。" +
            "若存在问题，输出修正后的完整 JSON（格式与输入一致：{\"text\":\"短文含[1]...[10]标记\",\"cn\":\"中文翻译（无[N]标记）\",\"candidates\":[{\"word\":\"屈折形式\",\"blank\":1},...,{\"word\":\"干扰词\",\"blank\":-1}]}）。" +
            "只输出 JSON。";

        /// <summary>流式请求时实时更新的 AI 思考原文。推理模式下预生成 Task 写入，PushMiniReading 读取。</summary>
        public static volatile string LiveThinking = null;

        /// <summary>最近一次 API 调用的失败原因。预生成 Task 写入，PushMiniReading 读取后展示给用户。</summary>
        public static volatile string LiveError = null;

        /// <summary>最近一次流式读取的诊断信息（行数、reasoning/content字节数等）。</summary>
        public static volatile string LiveDiag = null;

        /// <summary>最近一次流式读取是否因 max_tokens 耗尽被截断（finish_reason=length）。推理模式思考过长时触发。</summary>
        public static volatile bool LastTruncated = false;

        /// <summary>当前活跃的 HTTP 请求，供 AbortActiveRequest 超时主动中止。</summary>
        private static volatile HttpWebRequest _activeRequest = null;

        /// <summary>主动中止当前进行中的 AI 请求（生成超时 180 秒兜底用）。</summary>
        public static void AbortActiveRequest()
        {
            var req = _activeRequest;
            if (req != null) { try { req.Abort(); } catch { } }
        }

        /// <summary>
        /// <summary>
        /// SSE 流式读取响应，累积 content 并实时更新 LiveThinking（reasoning_content）。
        /// 返回累积的完整 content 文本。诊断信息写入 DiagLog。
        /// </summary>
        /// <summary>
        /// SSE 流式读取响应，累积 content 并实时更新 LiveThinking（reasoning_content）。
        /// 返回累积的完整 content 文本。诊断信息写入 LiveDiag。
        /// </summary>
        private static string ReadStreamingResponse(HttpWebResponse resp)
        {
            LiveThinking = "";
            string content = "";
            string reasoning = "";
            int lineCount = 0, reasoningChunks = 0, contentChunks = 0;
            bool gotDone = false;
            bool finishLength = false;

            try
            {
                using (var stream = resp.GetResponseStream())
                using (var sr = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        lineCount++;
                        if (!line.StartsWith("data: ")) continue;
                        string json = line.Substring(6).Trim();
                        if (json == "[DONE]") { gotDone = true; break; }

                        try
                        {
                            var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                            var choices = (System.Collections.ArrayList)doc["choices"];
                            if (choices.Count == 0) continue;
                            var c0 = (Dictionary<string, object>)choices[0];
                            var delta = (Dictionary<string, object>)c0["delta"];
                            object v;
                            if (delta.TryGetValue("reasoning_content", out v) && v is string && !string.IsNullOrEmpty((string)v))
                            {
                                reasoning += (string)v;
                                LiveThinking = reasoning;
                                reasoningChunks++;
                            }
                            if (delta.TryGetValue("content", out v) && v is string)
                            {
                                content += (string)v;
                                contentChunks++;
                            }
                            object fr;
                            if (c0.TryGetValue("finish_reason", out fr) && fr is string && (string)fr == "length")
                                finishLength = true;
                        }
                        catch { /* 个别 chunk 解析失败不中断流 */ }
                    }
                }
            }
            catch (Exception ex)
            {
                LiveDiag = string.Format("lines={0} reason={1}B content={2}B EX={3}",
                    lineCount, reasoning.Length, content.Length, ex.Message);
                LiveThinking = null;
                LiveError = "流读取异常: " + ex.Message;
                LastTruncated = false;
                return content;
            }

            LiveDiag = string.Format("lines={0} reason={1}B({2}c) content={3}B({4}c) done={5} trunc={6}",
                lineCount, reasoning.Length, reasoningChunks, content.Length, contentChunks, gotDone, finishLength);
            LiveThinking = null;
            LastTruncated = finishLength;
            return content;
        }

        public static bool TryGenerate(List<string> words, string bookName, out EssayResult result, out string error)
        {
            result = null; error = null;
            LiveError = null;
            AiConfig.Load();
            if (string.IsNullOrEmpty(AiConfig.ApiKey)) { error = "未设置 API Key"; LiveError = error; return false; }

            try
            {
                // 固定 system prompt + user 仅发单词表：
                // 1) DeepSeek 对相同前缀自动命中上下文缓存（缓存输入价格约 1/10）
                // 2) 每次请求的变动部分只有十几个 token
                // response_format=json_object：API 层强制输出合法 JSON，消灭格式漂移
                const string SYS_PROMPT =
                    "用户给出英语单词列表。完成两件事：" +
                    "1)用全部单词（允许屈折变化形式）写一篇自然连贯的英语短文（120~180词），" +
                    "文体贴近{LEVEL}阅读：有具体细节、因果/转折等逻辑关系，避免流水账。" +
                    "2)根据短文出3道{LEVEL}水准的英文阅读理解单选题，命题要求：" +
                    "a.正确选项必须对原文同义替换或改写，禁止照抄原文词句；" +
                    "b.干扰项要有迷惑性——可用原文出现过的词但表述错误、偷换概念、张冠李戴或过度推断，禁止一眼排除的选项；" +
                    "c.3题题型必须不同，从中选取：细节理解、推理判断(infer/imply/suggest)、主旨大意、词义猜测(the word \\\"X\\\" most likely means)、作者态度；" +
                    "若出词义猜测题，被猜的词X必须是短文中出现但【不在用户给定单词列表内】的生僻词，禁止用给定单词；" +
                    "d.每题4个选项（每个6~10个单词），只有1个正确，正确选项位置随机；4个选项长度必须严格均衡——单词数相同或仅相差1个，正确选项不得长于任何干扰项；为避免正确项天然偏长，正确选项要写得精炼，干扰项也必须写成结构完整的句子而非短语片段；任何选项明显偏长都会直接泄露答案，属于严重命题错误。" +
                    "以JSON输出：{\"en\":\"英文短文\",\"cn\":\"短文的中文翻译\"," +
                    "\"questions\":[{\"q\":\"英文题目\",\"choices\":[\"选项1\",\"选项2\",\"选项3\",\"选项4\"],\"answer\":0}]}" +
                    "，answer是正确选项下标(0~3)。只输出JSON。";

                // 推理模式专用 prompt：不限字数、自由发挥。命题要求与快速模式一致。
                // 推理模式不发 response_format（thinking 下不保证支持）→ 显式禁止 markdown 包裹兜底。
                const string SYS_PROMPT_REASON =
                    "用户给出英语单词列表。完成两件事：" +
                    "1)用全部单词（允许屈折变化形式）写一篇自然连贯的英语短文，不设字数限制，" +
                    "篇幅按内容需要自由发挥、充分展开，不必刻意精简，" +
                    "文体贴近{LEVEL}阅读：有具体细节、因果/转折等逻辑关系，内容有深度、值得一读，避免流水账。" +
                    "2)根据短文出3道{LEVEL}水准的英文阅读理解单选题，命题要求：" +
                    "a.正确选项必须对原文同义替换或改写，禁止照抄原文词句；" +
                    "b.干扰项要有迷惑性——可用原文出现过的词但表述错误、偷换概念、张冠李戴或过度推断，禁止一眼排除的选项；" +
                    "c.3题题型必须不同，从中选取：细节理解、推理判断(infer/imply/suggest)、主旨大意、词义猜测(the word \\\"X\\\" most likely means)、作者态度；" +
                    "若出词义猜测题，被猜的词X必须是短文中出现但【不在用户给定单词列表内】的生僻词，禁止用给定单词；" +
                    "d.每题4个选项（每个6~10个单词），只有1个正确，正确选项位置随机；4个选项长度必须严格均衡——单词数相同或仅相差1个，正确选项不得长于任何干扰项；为避免正确项天然偏长，正确选项要写得精炼，干扰项也必须写成结构完整的句子而非短语片段；任何选项明显偏长都会直接泄露答案，属于严重命题错误。" +
                    "以JSON输出：{\"en\":\"英文短文\",\"cn\":\"短文的中文翻译\"," +
                    "\"questions\":[{\"q\":\"英文题目\",\"choices\":[\"选项1\",\"选项2\",\"选项3\",\"选项4\"],\"answer\":0}]}" +
                    "，answer是正确选项下标(0~3)。只输出纯JSON文本，禁止用markdown代码块包裹。";

                // 生成轮：system prompt 按当前词库难度动态化 + user 仅发单词表（同词库内命中上下文缓存）
                string level = GetBookLevel(bookName);
                string sysPrompt = SYS_PROMPT.Replace("{LEVEL}", level);
                string sysPromptReason = SYS_PROMPT_REASON.Replace("{LEVEL}", level);
                bool reason = AiConfig.ModelMode == 1;
                string body = BuildBody(reason ? sysPromptReason : sysPrompt,
                    string.Join(", ", words.ToArray()), reason, 1400);

                // 局部函数：推理模式失败（思考过长截断/解析失败）→ 快速模式兜底重试。
                // 快速模式 thinking disabled + json_object，不会被思考挤占，成功率极高。
                bool TryFastFallback(out EssayResult fb)
                {
                    fb = null;
                    string fbBody = BuildBody(sysPrompt, string.Join(", ", words.ToArray()), false, 1400);
                    string fbContent, fbError;
                    if (!PostChat(fbBody, false, out fbContent, out fbError)) return false;
                    if (!ParseResult(fbContent, out fb, out fbError)) return false;
                    ReviseEssay(words, bookName, ref fb);
                    return true;
                }

                string content;
                if (!PostChat(body, reason, out content, out error))
                {
                    if (reason && TryFastFallback(out result)) return true;
                    return false;
                }
                if (!ParseResult(content, out result, out error))
                {
                    LiveError = error;
                    LogFailedContent("ESSAY", words, content, error);
                    if (reason && TryFastFallback(out result)) return true;
                    return false;
                }

                // 修正轮：仅快速模式（推理模式 pro 本身深度思考，保持单轮控延迟/成本）
                if (!reason)
                    ReviseEssay(words, bookName, ref result);

                return true;
            }
            catch (WebException we)
            {
                var r = we.Response as HttpWebResponse;
                if (r != null)
                    error = HttpErrorDetail((int)r.StatusCode, ReadResponseBodySafely(we.Response));
                else
                    error = "网络错误: " + we.Message;
                LiveError = error; return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                LiveError = error; return false;
            }
        }

        /// <summary>
        /// 15选10 完形填空生成。
        /// AI 写短文（含10个挖空标记）+ 15个候选词（10正+5干扰），
        /// 干扰词由 AI 设计保证语义/词形匹配，非本地随机抽取。
        /// </summary>
        public static bool TryGenerateCloze(List<string> words, string bookName, out ClozeResult result, out string error)
        {
            result = null; error = null;
            LiveError = null;
            AiConfig.Load();
            if (string.IsNullOrEmpty(AiConfig.ApiKey)) { error = "未设置 API Key"; LiveError = error; return false; }

            try
            {
                // Prompt 设计要点：
                // - 短文 150-200 词，含 10 个空
                // - 至少 5 个空来自用户词表（用正确的屈折形式）
                // - 15 候选 = 10 正 + 5 干扰
                // - 干扰词要求：同词性、形近/语义相近、符合空位屈折要求
                const string CLOZE_PROMPT =
                    "设计一道15选10选词填空题（类似四六级Banked Cloze）。" +
                    "用户给出一份单词列表，这是他们**刚刚学完的词，应当比较熟悉**。你需要写一篇短文，" +
                    "把这些词自然地融入文中，让用户在阅读时看到这些词、巩固记忆。" +
                    "" +
                    "★★★ 关键原则：挖空与否只看这个词在文中有没有辨析价值，不看它是否在用户列表里 ★★★" +
                    "不要刻意挖用户词、也不要刻意避开用户词。从短文本身出发，选出10个最值得挖的实词即可。" +
                    "" +
                    "要求：" +
                    "1) 写一篇150~200词的英文短文（{LEVEL}阅读难度，有逻辑有细节）。" +
                    "**必须**包含用户列表中的每一个词（允许屈折变化，如adopt→adopted；允许派生，如strategy→strategic）。" +
                    "把它们自然地融入，不要让它们显得突兀或堆砌。" +
                    "" +
                    "2) 从短文中选出**恰好10个实词**（名/动/形/副）挖空，用[1][2]...[10]依次标记。选词只看两点：" +
                    "a) 这个词在上下文中有推断空间——前后句的语义、逻辑连接、固定搭配能提供足够线索；" +
                    "b) 这个词有辨析价值——存在词性相同、但语义或固定搭配不同的替换词（可用来做干扰项，不要求是近义词）。不选专有名词、数字、冠词。" +
                    "" +
                    "★★★ 最重要规则：[N] 是替换符，它完全取代了原文中的一个词 ★★★" +
                    "挖空后，该词的字母必须从正文 text 里彻底消失，只留下一个光秃秃的 [N]。答案词**只允许**出现在 candidates 数组的 word 字段里，正文 text 中绝不能出现任何答案词。" +
                    "错误写法（严禁，等于把答案亮出来）：...compelled me to [1] revise that assumption.（[1] 后紧跟答案 revise）" +
                    "正确写法：...compelled me to [1] that assumption.（revise 已从正文消失，只在 candidates 里）" +
                    "输出前请逐空自检：把正文里每个 [N] 后面紧跟的那个词删掉——若删掉后句子读不通或语义改变，说明你写错了，必须重写该空。" +
                    "" +
                    "3) 制作**恰好15个候选词**：10个正确答案（各空位在文中的确切屈折形式）+ 5个干扰词。" +
                    "★★★ 干扰词必须模仿四六级真题的干扰方式，而非近义词辨析 ★★★" +
                    "真题靠词性混淆、搭配不当、语义偏离制造干扰，让考生先按词性分组、再靠语义和固定搭配排除；严格近义词辨析在真题里只占极少数，且语境区分明显。" +
                    "干扰词设计标准（每条都必须满足）：" +
                    "a) 词性必须与某个空位相同（名/动/形/副匹配），使考生无法只靠词性直接排除；" +
                    "b) 优先用三种干扰方式之一：①搭配不当（动宾/介词/主谓搭配错误，语法通但英语母语者不会这么说）；②语义偏离（词性对、意思沾边但语境不符——情感色彩、程度、范围、对象错误）；③词形或词性易混（-ed/-ing 形容词、名词单复数、动词时态、形近词）；" +
                    "c) 严格近义词（如 examine/inspect、flaw/defect、precision/accuracy）做干扰项最多 1 个，且该近义词在语境中必须有明显区分（情感、语体、搭配对象不同）；" +
                    "d) **使用与空位一致的屈折形式**（如空位需过去式 adopted，干扰词也是过去式 adapted，不能是 adapt）；" +
                    "e) 5个干扰词各自瞄准**不同的**空位，分散干扰，不要扎堆攻击同一个空位；" +
                    "f) **严禁**将用户列表中的词作为干扰词——用户刚学过，一眼能排除，毫无迷惑性。" +
                    "" +
                    "4) 给出短文的中文翻译。★★★ 翻译中不要保留[1]...[10]这些占位符！★★★" +
                    "直接把挖空词对应的中文填入译文，使译文通顺完整、像正常中文段落一样。用户看翻译是为了理解短文含义，带洞的翻译没有意义。" +
                    "" +
                    "输出纯JSON，格式：" +
                    "{\"text\":\"短文含[1]...[10]标记\",\"cn\":\"中文翻译（无[N]标记，所有空位已填入对应中文词）\"," +
                    "\"candidates\":[{\"word\":\"正确屈折形式\",\"blank\":1},...,{\"word\":\"正确屈折形式\",\"blank\":10},{\"word\":\"干扰词\",\"blank\":-1},...]}" +
                    "candidates必须恰好15项。blank=1~10为正确答案，-1为干扰词。只输出JSON。";

                const string CLOZE_PROMPT_REASON =
                    "设计一道15选10选词填空题（类似四六级Banked Cloze）。" +
                    "用户给出一份单词列表，这是他们**刚刚学完的词，应当比较熟悉**。你需要写一篇短文，" +
                    "把这些词自然地融入文中，让用户在阅读时看到这些词、巩固记忆。" +
                    "" +
                    "★★★ 关键原则：挖空与否只看这个词在文中有没有辨析价值，不看它是否在用户列表里 ★★★" +
                    "不要刻意挖用户词、也不要刻意避开用户词。从短文本身出发，选出10个最值得挖的实词即可。" +
                    "" +
                    "要求：" +
                    "1) 写一篇英文短文（不设字数限制，按内容需要充分展开）。" +
                    "**必须**包含用户列表中的每一个词（允许屈折变化，如adopt→adopted；允许派生，如strategy→strategic）。" +
                    "把它们自然地融入，不要让它们显得突兀或堆砌。短文有实质内容和思想深度，{LEVEL}以上阅读水平，值得一读。" +
                    "" +
                    "2) 从短文中选出**恰好10个实词**（名/动/形/副）挖空，用[1][2]...[10]依次标记。选词只看两点：" +
                    "a) 这个词在上下文中有推断空间——前后句的语义、逻辑连接、固定搭配能提供足够线索；" +
                    "b) 这个词有辨析价值——存在词性相同、但语义或固定搭配不同的替换词（可用来做干扰项，不要求是近义词）。不选专有名词、数字、冠词。" +
                    "" +
                    "★★★ 最重要规则：[N] 是替换符，它完全取代了原文中的一个词 ★★★" +
                    "挖空后，该词的字母必须从正文 text 里彻底消失，只留下一个光秃秃的 [N]。答案词**只允许**出现在 candidates 数组的 word 字段里，正文 text 中绝不能出现任何答案词。" +
                    "错误写法（严禁，等于把答案亮出来）：...compelled me to [1] revise that assumption.（[1] 后紧跟答案 revise）" +
                    "正确写法：...compelled me to [1] that assumption.（revise 已从正文消失，只在 candidates 里）" +
                    "输出前请逐空自检：把正文里每个 [N] 后面紧跟的那个词删掉——若删掉后句子读不通或语义改变，说明你写错了，必须重写该空。" +
                    "" +
                    "3) 制作**恰好15个候选词**：10个正确答案（各空位在文中的确切屈折形式）+ 5个干扰词。" +
                    "★★★ 干扰词必须模仿四六级真题的干扰方式，而非近义词辨析 ★★★" +
                    "真题靠词性混淆、搭配不当、语义偏离制造干扰，让考生先按词性分组、再靠语义和固定搭配排除；严格近义词辨析在真题里只占极少数，且语境区分明显。" +
                    "干扰词设计标准（每条都必须满足）：" +
                    "a) 词性必须与某个空位相同（名/动/形/副匹配），使考生无法只靠词性直接排除；" +
                    "b) 优先用三种干扰方式之一：①搭配不当（动宾/介词/主谓搭配错误，语法通但英语母语者不会这么说）；②语义偏离（词性对、意思沾边但语境不符——情感色彩、程度、范围、对象错误）；③词形或词性易混（-ed/-ing 形容词、名词单复数、动词时态、形近词）；" +
                    "c) 严格近义词（如 examine/inspect、flaw/defect、precision/accuracy）做干扰项最多 1 个，且该近义词在语境中必须有明显区分（情感、语体、搭配对象不同）；" +
                    "d) **使用与空位一致的屈折形式**（如空位需过去式 adopted，干扰词也是过去式 adapted，不能是 adapt）；" +
                    "e) 5个干扰词各自瞄准**不同的**空位，分散干扰，不要扎堆攻击同一个空位；" +
                    "f) **严禁**将用户列表中的词作为干扰词——用户刚学过，一眼能排除，毫无迷惑性。" +
                    "" +
                    "4) 给出短文的中文翻译。★★★ 翻译中不要保留[1]...[10]这些占位符！★★★" +
                    "直接把挖空词对应的中文填入译文，使译文通顺完整、像正常中文段落一样。用户看翻译是为了理解短文含义，带洞的翻译没有意义。" +
                    "" +
                    "输出纯JSON文本，格式：" +
                    "{\"text\":\"短文含[1]...[10]标记\",\"cn\":\"中文翻译（无[N]标记，所有空位已填入对应中文词）\"," +
                    "\"candidates\":[{\"word\":\"正确屈折形式\",\"blank\":1},...,{\"word\":\"正确屈折形式\",\"blank\":10},{\"word\":\"干扰词\",\"blank\":-1},...]}" +
                    "candidates必须恰好15项。blank=1~10为正确答案，-1为干扰词。" +
                    "只输出纯JSON，**禁止用markdown代码块包裹**。";

                // 生成轮：system prompt 按当前词库难度动态化 + user 仅发单词表（同词库内命中上下文缓存）
                string level = GetBookLevel(bookName);
                string clozePrompt = CLOZE_PROMPT.Replace("{LEVEL}", level);
                string clozePromptReason = CLOZE_PROMPT_REASON.Replace("{LEVEL}", level);
                bool reason = AiConfig.ModelMode == 1;
                string body = BuildBody(reason ? clozePromptReason : clozePrompt,
                    string.Join(", ", words.ToArray()), reason, 2000);

                // 局部函数：推理模式失败 → 快速模式兜底重试（thinking disabled 不会被思考挤占）。
                bool TryFastFallback(out ClozeResult fb)
                {
                    fb = null;
                    string fbBody = BuildBody(clozePrompt, string.Join(", ", words.ToArray()), false, 2000);
                    string fbContent, fbError;
                    if (!PostChat(fbBody, false, out fbContent, out fbError)) return false;
                    if (!ParseClozeResult(fbContent, out fb, out fbError)) return false;
                    ReviseCloze(words, bookName, ref fb);
                    return true;
                }

                string content;
                if (!PostChat(body, reason, out content, out error))
                {
                    if (reason && TryFastFallback(out result)) return true;
                    return false;
                }
                if (!ParseClozeResult(content, out result, out error))
                {
                    LiveError = error;
                    LogFailedContent("CLOZE", words, content, error);
                    if (reason && TryFastFallback(out result)) return true;
                    return false;
                }

                // 修正轮：仅快速模式（推理模式保持单轮）
                if (!reason)
                    ReviseCloze(words, bookName, ref result);

                return true;
            }
            catch (WebException we)
            {
                var r = we.Response as HttpWebResponse;
                if (r != null)
                    error = HttpErrorDetail((int)r.StatusCode, ReadResponseBodySafely(we.Response));
                else
                    error = "网络错误: " + we.Message;
                LiveError = error; return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                LiveError = error; return false;
            }
        }

        // ===== 多轮自检生成辅助方法（2026-08-08）=====

        /// <summary>构造一次 /chat/completions 请求体。reason=true 用推理模型（stream + 16000）；否则快速模型（json_object + thinking disabled + maxTokens）。</summary>
        private static string BuildBody(string sysPrompt, string userContent, bool reason, int maxTokens)
        {
            if (reason)
                return "{\"model\":\"" + JsonEscape(AiConfig.ReasonModel) + "\"," +
                    "\"messages\":[{\"role\":\"system\",\"content\":\"" + JsonEscape(sysPrompt) + "\"}," +
                    "{\"role\":\"user\",\"content\":\"" + JsonEscape(userContent) + "\"}]," +
                    "\"stream\":true,\"max_tokens\":32000}";
            return "{\"model\":\"" + JsonEscape(AiConfig.Model) + "\"," +
                "\"messages\":[{\"role\":\"system\",\"content\":\"" + JsonEscape(sysPrompt) + "\"}," +
                "{\"role\":\"user\",\"content\":\"" + JsonEscape(userContent) + "\"}]," +
                "\"response_format\":{\"type\":\"json_object\"}," +
                "\"thinking\":{\"type\":\"disabled\"}," +
                "\"temperature\":0.7,\"max_tokens\":" + maxTokens + "}";
        }

        /// <summary>发送一次 chat/completions 并读回原始响应文本。reason=true 走 SSE 流式（更新 LiveThinking）；false 非流式整读。返回 false 时 error 含原因。</summary>
        private static bool PostChat(string body, bool reason, out string content, out string error)
        {
            content = null; error = null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(AiConfig.BaseUrl.TrimEnd('/') + "/chat/completions");
                _activeRequest = req;   // 供 AbortActiveRequest 超时主动中止
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers.Add("Authorization", "Bearer " + AiConfig.ApiKey);
                req.KeepAlive = false;  // 流式响应必须关 KeepAlive，否则 ReadLine 读完 [DONE] 后死等 TCP 不超时
                int timeoutMs = reason ? TIMEOUT_REASON_MS : TIMEOUT_FAST_MS;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;

                byte[] data = Encoding.UTF8.GetBytes(body);
                using (var rs = req.GetRequestStream())
                    rs.Write(data, 0, data.Length);

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (reason)
                        content = ReadStreamingResponse(resp);
                    else
                        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            content = (string)((Dictionary<string, object>)((Dictionary<string, object>)((System.Collections.ArrayList)
                                new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(sr.ReadToEnd())["choices"])[0])["message"])["content"];
                }
                return true;
            }
            catch (WebException we)
            {
                var r = we.Response as HttpWebResponse;
                if (r != null)
                    error = HttpErrorDetail((int)r.StatusCode, ReadResponseBodySafely(we.Response));
                else
                    error = "网络错误: " + we.Message;
                LiveError = error; return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                LiveError = error; return false;
            }
        }

        /// <summary>Essay 修正轮：把当前结果序列化发给模型自检，模型满意回 {"ok":true} 则保持，否则接受修正版。失败一律回退上一版。</summary>
        private static void ReviseEssay(List<string> words, string bookName, ref EssayResult result)
        {
            for (int round = 0; round < MAX_REVISE_ROUNDS; round++)
            {
                string user = string.Join(", ", words.ToArray()) + "\n\n当前短文与题目(JSON):\n" + SerializeEssay(result);
                string body = BuildBody(REVISE_PROMPT.Replace("{LEVEL}", GetBookLevel(bookName)), user, false, 1400);
                string c, e;
                if (!PostChat(body, false, out c, out e))
                    break;
                if (IsOkFlag(c))
                    break;
                EssayResult revised;
                string rerr;
                if (ParseResult(c, out revised, out rerr))
                    result = revised;
                else
                    break;
            }
        }

        /// <summary>CLOZE 修正轮：同上。</summary>
        private static void ReviseCloze(List<string> words, string bookName, ref ClozeResult result)
        {
            for (int round = 0; round < MAX_REVISE_ROUNDS; round++)
            {
                string user = string.Join(", ", words.ToArray()) + "\n\n当前短文与题目(JSON):\n" + SerializeCloze(result);
                string body = BuildBody(REVISE_CLOZE_PROMPT.Replace("{LEVEL}", GetBookLevel(bookName)), user, false, 2000);
                string c, e;
                if (!PostChat(body, false, out c, out e))
                    break;
                if (IsOkFlag(c))
                    break;
                ClozeResult revised;
                string rerr;
                if (ParseClozeResult(c, out revised, out rerr))
                    result = revised;
                else
                    break;
            }
        }

        /// <summary>把 EssayResult 序列化为 JSON（发给修正轮用）。</summary>
        private static string SerializeEssay(EssayResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"en\":").Append(JsonEscape(r.EssayEN));
            sb.Append(",\"cn\":").Append(JsonEscape(r.EssayCN ?? ""));
            sb.Append(",\"questions\":[");
            for (int i = 0; i < r.Questions.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var q = r.Questions[i];
                sb.Append("{\"q\":").Append(JsonEscape(q.Question));
                sb.Append(",\"choices\":[");
                for (int j = 0; j < q.Choices.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    sb.Append(JsonEscape(q.Choices[j]));
                }
                sb.Append("],\"answer\":").Append(q.Answer).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>把 ClozeResult 序列化为 JSON（发给修正轮用）。</summary>
        private static string SerializeCloze(ClozeResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"text\":").Append(JsonEscape(r.Text));
            sb.Append(",\"cn\":").Append(JsonEscape(r.TextCN ?? ""));
            sb.Append(",\"candidates\":[");
            for (int i = 0; i < r.Candidates.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"word\":").Append(JsonEscape(r.Candidates[i].Word))
                  .Append(",\"blank\":").Append(r.Candidates[i].Blank).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>判断修正轮响应是否为 {"ok":true}（模型满意，用上一版，省输出 token）。</summary>
        private static bool IsOkFlag(string content)
        {
            try
            {
                int lb = content.IndexOf('{');
                int rb = content.LastIndexOf('}');
                if (lb < 0 || rb < lb) return false;
                var doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(content.Substring(lb, rb - lb + 1));
                object v;
                return doc != null && doc.TryGetValue("ok", out v) && v is bool && (bool)v;
            }
            catch { return false; }
        }

        private static bool ParseClozeResult(string content, out ClozeResult result, out string error)
        {
            result = null; error = null;
            if (string.IsNullOrEmpty(content)) { error = "AI 返回内容为空"; return false; }

            int lb = content.IndexOf('{');
            int rb = content.LastIndexOf('}');
            if (lb < 0 || rb <= lb) { error = "AI 返回的不是 JSON"; return false; }
            content = content.Substring(lb, rb - lb + 1);

            Dictionary<string, object> doc;
            try
            {
                doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(content);
            }
            catch (Exception e)
            {
                error = "JSON 解析失败: " + e.Message;
                return false;
            }
            if (doc == null) { error = "JSON 解析结果为空"; return false; }

            var r = new ClozeResult();
            r.Text = GetStr(doc, "text");
            r.TextCN = System.Text.RegularExpressions.Regex.Replace(
                GetStr(doc, "cn") ?? "", @"\[\d+\]", "");  // 防御性清除[N]标记，确保中文翻译通顺完整
            if (string.IsNullOrEmpty(r.Text) || r.Text.Length < 50)
            {
                error = "AI 返回短文缺失或过短";
                return false;
            }

            // 校验空位标记：必须含 [1]~[10]（AI 返回 1-based）
            int blankCount = 0;
            for (int i = 1; i <= 10; i++)
                if (r.Text.Contains("[" + i + "]")) blankCount++;
            if (blankCount < 7) { error = "AI 返回短文空位不足（需≥7个，实际" + blankCount + "）"; return false; }

            // 解析 candidates — AI 返回的 blank 是 1-based（1~10），内部转为 0-based（0~9）
            object csObj;
            var cs = doc.TryGetValue("candidates", out csObj) ? csObj as System.Collections.ArrayList : null;
            if (cs == null || cs.Count < 10) { error = "候选词不足"; return false; }

            var usedBlanks = new HashSet<int>();
            foreach (object co in cs)
            {
                var cd = co as Dictionary<string, object>;
                if (cd == null) continue;
                var c = new ClozeCandidate();
                c.Word = GetStr(cd, "word") ?? "";
                if (string.IsNullOrEmpty(c.Word)) continue;
                object bObj;
                if (cd.TryGetValue("blank", out bObj))
                {
                    try { c.Blank = Convert.ToInt32(bObj); } catch { c.Blank = -1; }
                }
                // AI 返回 1-based blank（1~10），转为内部 0-based（0~9）
                if (c.Blank >= 1 && c.Blank <= 10)
                {
                    int blank0 = c.Blank - 1;
                    if (usedBlanks.Contains(blank0)) continue; // 重复空位丢弃
                    usedBlanks.Add(blank0);
                    c.Blank = blank0;
                }
                else
                {
                    c.Blank = -1; // 干扰词
                }
                r.Candidates.Add(c);
            }

            // ★ 答案泄漏检测：正确答案词不能出现在短文正文（非空位部分）中
            // 先剥离所有 [N] 标记，再提取单词集合，然后逐一比对
            string textStripped = System.Text.RegularExpressions.Regex.Replace(r.Text, @"\[\d+\]", " ");
            var wordsInText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string w in System.Text.RegularExpressions.Regex.Split(textStripped, @"[^a-zA-Z]+"))
                if (w.Length > 1) wordsInText.Add(w);
            var leaked = new List<string>();
            foreach (var c in r.Candidates)
            {
                if (c.Blank >= 0 && !string.IsNullOrEmpty(c.Word) && c.Word.Length > 1
                    && wordsInText.Contains(c.Word))
                    leaked.Add(c.Word);
            }
            if (leaked.Count > 0)
            {
                error = "AI 答案泄漏：以下正确词出现在短文正文中——" + string.Join(", ", leaked);
                return false;
            }

            // 质量检查：至少 6 个正确词（太少就没法做任何填空了）
            int correctCount = r.Candidates.FindAll(c => c.Blank >= 0).Count;
            int distractorCount = r.Candidates.FindAll(c => c.Blank < 0).Count;
            if (correctCount < 4 || distractorCount < 1) { error = "候选词不足"; return false; }

            // 补足正确词+干扰词到至少 12 个（保底可用），占位干扰词不会伤害功能
            int fillerIdx = 0;
            while (correctCount + distractorCount < 12)
            {
                var fillers = new[] { "declined", "rejected", "observed", "performed", "established", "processed", "promoted" };
                r.Candidates.Add(new ClozeCandidate { Word = fillers[fillerIdx % fillers.Length], Blank = -1 });
                fillerIdx++;
                distractorCount++;
            }

            // Fisher-Yates 打乱候选词顺序
            ShuffleCandidates(r.Candidates);

            result = r;
            return true;
        }

        /// <summary>打乱候选词顺序（Fisher-Yates）</summary>
        private static void ShuffleCandidates(List<ClozeCandidate> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _shuffleRng.Next(i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        /// <summary>
        /// 将 HTTP 状态码映射为用户可读的中文错误提示。
        /// 同时尝试读取响应体中的 JSON 错误详情（DeepSeek 兼容格式）。
        /// </summary>
        private static string HttpErrorDetail(int statusCode, string responseBody)
        {
            string detail = "";
            // 尝试解析 DeepSeek 风格的 JSON 错误消息
            if (!string.IsNullOrEmpty(responseBody))
            {
                try
                {
                    var errDoc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(responseBody);
                    object msgObj;
                    if (errDoc.TryGetValue("error", out msgObj))
                    {
                        var errInner = msgObj as Dictionary<string, object>;
                        if (errInner != null && errInner.TryGetValue("message", out msgObj))
                            detail = ": " + (string)msgObj;
                        else
                            detail = ": " + (msgObj as string ?? "");
                    }
                    if (string.IsNullOrEmpty(detail) && errDoc.TryGetValue("message", out msgObj))
                        detail = ": " + (msgObj as string ?? "");
                }
                catch { }
            }

            switch (statusCode)
            {
                case 401: return "API Key 无效或未设置（401）" + detail;
                case 402: return "账户余额不足，请充值（402）" + detail;
                case 403: return "API 访问被拒绝，检查 Key 权限（403）" + detail;
                case 429: return "请求太频繁，稍后再试（429）" + detail;
                case 500: return "AI 服务器内部错误（500）" + detail;
                case 502: return "AI 服务网关异常（502）" + detail;
                case 503: return "AI 服务暂时过载，稍后再试（503）" + detail;
                case 504: return "AI 服务响应超时（504）" + detail;
                default: return "HTTP " + statusCode + detail;
            }
        }

        /// <summary>HTTP 请求失败时读取响应体文本（用于错误详情）。</summary>
        private static string ReadResponseBodySafely(WebResponse response)
        {
            try
            {
                using (var sr = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        /// <summary>将 AI 返回的原始内容写入失败日志，方便排查解析失败原因。</summary>
        private static void LogFailedContent(string type, List<string> words, string content, string error)
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "Resources", "ai_failed_content.log");
                var sb = new StringBuilder();
                sb.AppendLine("========================================");
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + type
                    + " FAIL [modelMode=" + AiConfig.ModelMode + "]");
                sb.AppendLine("--- words (" + words.Count + ") ---");
                sb.AppendLine(string.Join(", ", words.ToArray()));
                sb.AppendLine("--- error ---");
                sb.AppendLine(error);
                sb.AppendLine("--- raw content (" + (content != null ? content.Length : 0) + " chars) ---");
                sb.AppendLine(content ?? "(null)");
                sb.AppendLine("========================================");
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 日志写入失败不应影响主流程 */ }
        }

        /// 1) response_format=json_object 已在 API 层保证合法 JSON；
        /// 2) 仍截取首个 '{' 到末尾 '}' 兜底（防 markdown 代码块包裹等意外）；
        /// 3) 逐字段校验：en 缺失/过短 → 整体失败；questions 逐题校验，坏题丢弃、好题保留；
        ///    Questions 可能为空列表 —— 调用方回退抽词测验，短文仍可用。
        /// </summary>
        private static bool ParseResult(string content, out EssayResult result, out string error)
        {
            result = null; error = null;
            if (string.IsNullOrEmpty(content)) { error = "AI 返回内容为空"; return false; }

            // 兜底截取 JSON 主体
            int lb = content.IndexOf('{');
            int rb = content.LastIndexOf('}');
            if (lb < 0 || rb <= lb) { error = "AI 返回的不是 JSON"; return false; }
            content = content.Substring(lb, rb - lb + 1);

            Dictionary<string, object> doc;
            try
            {
                doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(content);
            }
            catch (Exception e)
            {
                error = "JSON 解析失败: " + e.Message;
                return false;
            }
            if (doc == null) { error = "JSON 解析结果为空"; return false; }

            var r = new EssayResult();
            r.EssayEN = GetStr(doc, "en");
            r.EssayCN = GetStr(doc, "cn") ?? "";
            if (string.IsNullOrEmpty(r.EssayEN) || r.EssayEN.Length < 30)
            {
                error = "AI 返回短文缺失或过短";
                return false;
            }

            object qsObj;
            var qs = doc.TryGetValue("questions", out qsObj) ? qsObj as System.Collections.ArrayList : null;
            if (qs != null)
            {
                foreach (object qo in qs)
                {
                    var qd = qo as Dictionary<string, object>;
                    if (qd == null) continue;
                    var eq = new EssayQuestion();
                    eq.Question = GetStr(qd, "q") ?? GetStr(qd, "question");   // 容忍字段名漂移
                    object csObj;
                    var cs = qd.TryGetValue("choices", out csObj) ? csObj as System.Collections.ArrayList : null;
                    if (cs != null)
                        foreach (object c in cs)
                        {
                            string sc = c as string;
                            if (!string.IsNullOrEmpty(sc)) eq.Choices.Add(sc.Trim());
                        }
                    object ansObj;
                    if (qd.TryGetValue("answer", out ansObj))
                    {
                        try { eq.Answer = Convert.ToInt32(ansObj); } catch { eq.Answer = -1; }
                    }
                    // 坏题丢弃：题干/选项/答案任一不合法都不进结果
                    if (!string.IsNullOrEmpty(eq.Question) && eq.Choices.Count >= 2
                        && eq.Choices.Count <= 4 && eq.Answer >= 0 && eq.Answer < eq.Choices.Count)
                    {
                        ShuffleChoices(eq);  // 打乱 AI 输出的选项顺序
                        r.Questions.Add(eq);
                    }
                }
            }

            result = r;
            return true;
        }

        private static readonly Random _shuffleRng = new Random();

        /// <summary>
        /// Fisher-Yates 洗牌，打乱题目选项顺序并同步更新正确选项下标。
        /// 目的：消除 AI 输出中正确选项位置的系统性偏差。
        /// </summary>
        private static void ShuffleChoices(EssayQuestion q)
        {
            string correct = q.Choices[q.Answer];
            int n = q.Choices.Count;
            for (int i = n - 1; i > 0; i--)
            {
                int j = _shuffleRng.Next(i + 1);
                string tmp = q.Choices[i];
                q.Choices[i] = q.Choices[j];
                q.Choices[j] = tmp;
            }
            q.Answer = q.Choices.IndexOf(correct);
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
