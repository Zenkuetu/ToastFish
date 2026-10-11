using System;

namespace ToastFish.Model
{
    /// <summary>
    /// 英语词库表名的**唯一权威来源**（2026-10-08 抽取）。
    ///
    /// 背景：这份 17 个表名的数组原先在 3 个文件里各写了一份
    /// （Select.cs / StudyLogManager.cs / SyncProgressManager.cs），内容字节级完全相同。
    /// 新增或删除一套词库时必须同时改 3 处，**漏改任何一处都会导致统计/同步静默出错**。
    ///
    /// 现统一到此处；各文件保留本地字段名 EnglishTables 指向本表，使用点无需改动。
    /// 注意：本数组只读使用（foreach / Concat），请勿在外部修改元素。
    /// </summary>
    internal static class WordTables
    {
        /// <summary>16 张标准英语词库表 + KaoYan_3（考研大纲词频表），共 17 张</summary>
        public static readonly string[] English = {
            "CET4_1", "CET4_3", "CET6_1", "CET6_2", "CET6_3",
            "Level4_1", "Level4luan_2", "Level8_1", "Level8luan_2",
            "KaoYan_1", "KaoYan_2", "KaoYan_3",
            "IELTS_3", "TOEFL_2", "GRE_2", "GMAT_3", "SAT_2"
        };
    }
}
