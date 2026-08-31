using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ToastFish.Model.PushControl
{
    /// <summary>
    /// 词形匹配：判断例句中某个词是否为 headWord 的屈折/变体形式。
    /// 分层：精确 → 规则后缀还原 → 英美拼写变体 → 不规则动词(含前缀) → 名词不规则复数 → 连字符复合词。
    /// 与 Tools\scan_sentence_mismatch.py 中的 Python 版算法保持同构。
    /// </summary>
    public static class WordFormMatcher
    {
        private static readonly string[] SUFFIXES =
            { "ies", "ied", "ier", "iest", "es", "ed", "ing", "ly", "er", "est", "ic", "s", "d", "n" };

        // 不规则动词（变形 -> 原形），初始化于静态构造
        private static readonly Dictionary<string, string> Irregular = new Dictionary<string, string>();

        private const string IRR_RAW =
            "arise arose arisen|awake awoke awoken|bear bore borne|beat beat beaten|" +
            "become became become|begin began begun|bend bent bent|bind bound bound|" +
            "bite bit bitten|bleed bled bled|blow blew blown|break broke broken|" +
            "breed bred bred|bring brought brought|build built built|burn burnt burned|" +
            "buy bought bought|catch caught caught|choose chose chosen|cling clung clung|" +
            "come came come|creep crept crept|deal dealt dealt|dig dug dug|" +
            "dive dove dived|do did done|draw drew drawn|dream dreamt dreamed|" +
            "drink drank drunk|drive drove driven|dwell dwelt dwelt|eat ate eaten|" +
            "fall fell fallen|feed fed fed|feel felt felt|fight fought fought|" +
            "find found found|flee fled fled|fling flung flung|fly flew flown|" +
            "forbid forbade forbidden|forget forgot forgotten|forgive forgave forgiven|" +
            "freeze froze frozen|get got gotten|give gave given|go went gone|" +
            "grind ground ground|grow grew grown|hang hung hung|hear heard heard|" +
            "hide hid hidden|hold held held|keep kept kept|kneel knelt knelt|" +
            "know knew known|lay laid laid|lead led led|leap leapt leaped|" +
            "leave left left|lend lent lent|lie lay lain|light lit lit|" +
            "lose lost lost|make made made|mean meant meant|meet met met|" +
            "mistake mistook mistaken|mow mowed mown|overcome overcame overcome|" +
            "pay paid paid|prove proved proven|ride rode ridden|ring rang rung|" +
            "rise rose risen|run ran run|saw sawed sawn|say said said|see saw seen|" +
            "seek sought sought|sell sold sold|send sent sent|shake shook shaken|" +
            "shear sheared shorn|shine shone shone|shoot shot shot|show showed shown|" +
            "shrink shrank shrunk|sing sang sung|sink sank sunk|sit sat sat|" +
            "slay slew slain|sleep slept slept|slide slid slid|sling slung slung|" +
            "smell smelt smelled|sneak snuck sneaked|sow sowed sown|speak spoke spoken|" +
            "speed sped sped|spell spelt spelled|spend spent spent|spill spilt spilled|" +
            "spin spun spun|spit spat spat|spoil spoilt spoiled|spring sprang sprung|" +
            "stand stood stood|steal stole stolen|stick stuck stuck|sting stung stung|" +
            "stink stank stunk|stride strode stridden|strike struck struck|" +
            "strive strove striven|swear swore sworn|sweep swept swept|" +
            "swell swelled swollen|swim swam swum|swing swung swung|" +
            "take took taken|teach taught taught|tear tore torn|tell told told|" +
            "think thought thought|throw threw thrown|thrust thrust thrust|" +
            "tread trod trodden|understand understood understood|wake woke woken|" +
            "wear wore worn|weave wove woven|weep wept wept|win won won|" +
            "wind wound wound|wring wrung wrung|write wrote written";

        static WordFormMatcher()
        {
            foreach (string grp in IRR_RAW.Split('|'))
            {
                string[] parts = grp.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                string baseForm = parts[0];
                for (int i = 1; i < parts.Length; i++)
                    if (parts[i] != baseForm && !Irregular.ContainsKey(parts[i]))
                        Irregular[parts[i]] = baseForm;
            }
        }

        /// <summary>英美拼写变体归一化：aesthetic/esthetic, humour/humor, catalogue/catalog</summary>
        private static string VariantNorm(string s)
        {
            return s.Replace("ae", "e").Replace("oe", "e").Replace("our", "or").Replace("ogue", "og");
        }

        /// <summary>规则后缀还原：implemented->implement, allocating->allocate, studies->study, stopped->stop</summary>
        private static bool StemMatch(string hw, string w)
        {
            if (w == hw) return true;
            foreach (string suf in SUFFIXES)
            {
                if (!w.EndsWith(suf) || w.Length - suf.Length < 2) continue;
                string stem = w.Substring(0, w.Length - suf.Length);
                if (stem == hw) return true;
                if (stem + "e" == hw) return true;
                if (suf[0] == 'i' && stem + "y" == hw) return true;
                if (stem.Length > 2 && stem[stem.Length - 1] == stem[stem.Length - 2]
                    && stem.Substring(0, stem.Length - 1) == hw) return true;
            }
            return false;
        }

        /// <summary>不规则动词匹配（含前缀形式 withheld->withhold, misled->mislead）</summary>
        private static bool IrregularMatch(string hw, string w)
        {
            string baseForm;
            if (Irregular.TryGetValue(w, out baseForm) && baseForm == hw) return true;
            foreach (var kv in Irregular)
            {
                if (w.EndsWith(kv.Key) && hw.EndsWith(kv.Value)
                    && w.Substring(0, w.Length - kv.Key.Length) == hw.Substring(0, hw.Length - kv.Value.Length))
                    return true;
            }
            return false;
        }

        private static bool BaseMatch(string hw, string w)
        {
            if (StemMatch(hw, w) || IrregularMatch(hw, w)) return true;
            string nhw = VariantNorm(hw), nw = VariantNorm(w);
            if ((nhw != hw || nw != w) && StemMatch(nhw, nw)) return true;
            // 名词不规则复数：man->men（含复合词）, wolf->wolves, criterion<->criteria
            if (hw.Contains("man") && w == hw.Replace("man", "men")) return true;
            if (w.EndsWith("ves") && (w.Substring(0, w.Length - 3) + "f" == hw
                                   || w.Substring(0, w.Length - 3) + "fe" == hw)) return true;
            if (hw.EndsWith("ion") && w == hw.Substring(0, hw.Length - 2) + "a") return true;
            if (hw.EndsWith("a") && w == hw.Substring(0, hw.Length - 1) + "on") return true;
            return false;
        }

        /// <summary>单个句中词与 headWord 的分层匹配总入口</summary>
        public static bool WordMatch(string headWord, string sentWord)
        {
            string hw = headWord.ToLowerInvariant();
            string w = sentWord.ToLowerInvariant().Trim('\'');
            if (w.EndsWith("'s")) w = w.Substring(0, w.Length - 2);   // pope's -> pope
            if (BaseMatch(hw, w)) return true;
            if (w.Contains("-"))
            {
                if (BaseMatch(hw, w.Replace("-", ""))) return true;   // co-operate -> cooperate
                foreach (string part in w.Split('-'))
                    if (part.Length > 0 && BaseMatch(hw, part)) return true;  // multi-faceted -> facet
            }
            return false;
        }

        // 分词：字母开头，允许内部撇号/连字符（up-to-date, butcher's, cliché）
        private static readonly Regex TokenRe =
            new Regex(@"[^\W\d_][^\W\d_]*(?:['\-][^\W\d_]+)*", RegexOptions.Compiled);

        /// <summary>
        /// 在句子中查找 headWord 的（变形）出现位置。
        /// 找到返回 true 并输出字符区间；找不到返回 false（调用方应正常显示句子、不高亮）。
        /// </summary>
        public static bool TryFindInSentence(string headWord, string sentence, out int start, out int length)
        {
            start = -1; length = 0;
            if (string.IsNullOrEmpty(headWord) || string.IsNullOrEmpty(sentence)) return false;
            string hw = headWord.Trim();
            // 词组型 headWord：直接整体查找（不做变形）
            if (hw.Contains(" "))
            {
                int idx = sentence.IndexOf(hw, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                start = idx; length = hw.Length;
                return true;
            }
            foreach (Match m in TokenRe.Matches(sentence))
            {
                if (WordMatch(hw, m.Value))
                {
                    start = m.Index; length = m.Length;
                    return true;
                }
            }
            return false;
        }
    }
}
