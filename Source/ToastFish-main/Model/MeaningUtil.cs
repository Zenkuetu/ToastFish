using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ToastFish.Model
{
    /// <summary>
    /// #107（3.4.4）：中文释义的等价判断。
    ///
    /// 词库里存在「两个不同的词共用一个释义」的情况（本机实测：quiver 与 tremble 都译作「颤抖」，
    /// smoke 与 fume 都译作「烟」）。出「看释义选单词」的三选一时，如果把这种词当干扰项，
    /// 题目就会出现两个都对的选项，用户怎么选都可能被判错。
    ///
    /// 这里把释义按「、，；」等分隔符拆成含义片段，用片段交集判断两个词是否同义；
    /// 干扰项挑选统一走这个判断（见 PushWords.GetTransQuestionDistractors / Select.GetRandomJpWords）。
    /// </summary>
    public static class MeaningUtil
    {
        // 含义分隔符：中英文逗号、顿号、分号、斜杠、竖线、换行、括号与空白
        private static readonly Regex Splitter =
            new Regex(@"[、，,；;/\\|\n\r\t（）\(\)\[\]【】\s]+", RegexOptions.Compiled);

        // 词性前缀，如 "v." "n." "adj."（个别词库会写）
        private static readonly Regex PosPrefix =
            new Regex(@"^([a-zA-Z]{1,5}\.)+", RegexOptions.Compiled);

        /// <summary>把释义拆成去重后的含义片段（小写）。</summary>
        public static List<string> Parts(string tranCN)
        {
            List<string> parts = new List<string>();
            if (string.IsNullOrWhiteSpace(tranCN))
                return parts;

            // 省略号本身也是分隔（"为了…、因为…"）
            string s = tranCN.Replace("…", "、").Replace("...", "、");
            foreach (string raw in Splitter.Split(s))
            {
                string p = raw.Trim();
                p = PosPrefix.Replace(p, string.Empty).Trim();
                p = p.Trim('.', '。', ':', '：', '…', '"', '\'');
                if (p.Length == 0)
                    continue;
                p = p.ToLowerInvariant();
                if (!parts.Contains(p))
                    parts.Add(p);
            }
            return parts;
        }

        /// <summary>两个释义是否有任何一条含义相同。</summary>
        public static bool SameMeaning(string a, string b)
        {
            List<string> pa = Parts(a);
            List<string> pb = Parts(b);
            if (pa.Count == 0 || pb.Count == 0)
                return false;
            foreach (string x in pa)
            {
                if (pb.Contains(x))
                    return true;
            }
            return false;
        }
    }
}
