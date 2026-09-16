"""
数据库分发清洗脚本
将 inami.db 中所有学习数据重置为初始状态，保留完整词汇数据。
仅修改 staging 目录下的副本，绝不触碰真实数据库。

用法:
    python reset_database_for_distribution.py <staging_db_path>
    例: python reset_database_for_distribution.py E:\ToastFish.v3.0\Installer\staging\Resources\inami.db

2026-09-16 修复（见 CLAUDE.md #68）—— 此前存在的数据泄漏：
  1. EssayLog 完全未处理 -> 69 条 AI 短文历史（含短文原文/题目/作答）被打进安装包
  2. Global.pendingEssayWords（AI 短文累积列，2026-09-16 新增）未重置 -> 会带出开发者的累积进度
  3. Global 的 autoPlay / EngType / autoLog 未重置 -> 带出开发者的偏好设置
  4. dateLearingDue（#62 新增列）未重置 —— 该列由 SelectWordList 按需 ALTER，
     仅被程序打开过的词库才有，故所有列操作改为**动态判断**以兼容新旧表结构
  5. 下划线前缀的历史备份表（如 _CET6_3_old_20260810）未清理 -> 直接 DROP
"""
import sqlite3
import sys
import os
import shutil

# 复盘题列：KaoYan_3 等精简表没有，需动态判断
CHOICE_COLS = ['question', 'explain', 'rightIndex', 'examType',
               'choiceIndexOne', 'choiceIndexTwo', 'choiceIndexThree', 'choiceIndexFour']

# 全部英语词表（含 KaoYan_3 精简结构，统一动态处理）
ENG_TABLES = [
    'CET4_1', 'CET4_3', 'CET6_1', 'CET6_2', 'CET6_3',
    'GMAT_3', 'GRE_2', 'IELTS_3', 'SAT_2', 'TOEFL_2',
    'KaoYan_1', 'KaoYan_2', 'KaoYan_3', 'Level4_1', 'Level4luan_2',
    'Level8_1', 'Level8luan_2'
]


def table_exists(conn, table):
    return conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name=?", (table,)
    ).fetchone() is not None


def table_columns(conn, table):
    return [r[1] for r in conn.execute("PRAGMA table_info([%s])" % table).fetchall()]


def reset_word_table(conn, table):
    """重置单张词表的学习状态（列动态判断，兼容精简表结构）"""
    cols = table_columns(conn, table)
    sets = [
        "status = 0",
        "difficulty = 0.3",
        "daysBetweenReviews = 3",
        "lastScore = 0",
        "dateLastReviewed = NULL",
        "dateLastReviewed_bak = NULL",
    ]
    if 'dateLearingDue' in cols:
        sets.append("dateLearingDue = NULL")
    for c in CHOICE_COLS:
        if c in cols:
            sets.append("%s = NULL" % c)

    learned = conn.execute("SELECT COUNT(*) FROM [%s] WHERE status != 0" % table).fetchone()[0]
    total = conn.execute("SELECT COUNT(*) FROM [%s]" % table).fetchone()[0]
    conn.execute("UPDATE [%s] SET " % table + ", ".join(sets))
    print("  %s: %d/%d 已学 -> 已重置" % (table, learned, total))


def reset_database(db_path):
    if not os.path.exists(db_path):
        print("错误: 文件不存在 %s" % db_path)
        sys.exit(1)

    # 备份（安全起见）
    bak_path = db_path + ".before_reset_bak"
    shutil.copy2(db_path, bak_path)
    print("备份已保存: %s" % bak_path)

    conn = sqlite3.connect(db_path)
    conn.execute("PRAGMA journal_mode=OFF")  # 加速批量写入

    # ============ 1. 英语词表（17 张，动态适配结构） ============
    for table in ENG_TABLES:
        if table_exists(conn, table):
            reset_word_table(conn, table)
        else:
            print("  跳过: %s (不存在)" % table)

    # ============ 2. 日语词表 StdJp_Mid ============
    if table_exists(conn, 'StdJp_Mid'):
        cols = table_columns(conn, 'StdJp_Mid')
        sets = ["status = 0"]
        if 'phone' in cols:
            sets.append("phone = NULL")
        learned = conn.execute("SELECT COUNT(*) FROM StdJp_Mid WHERE status != 0").fetchone()[0]
        total = conn.execute("SELECT COUNT(*) FROM StdJp_Mid").fetchone()[0]
        conn.execute("UPDATE StdJp_Mid SET " + ", ".join(sets))
        print("  StdJp_Mid: %d/%d 已学 -> 已重置" % (learned, total))

    # ============ 3. 五十音 Goin ============
    if table_exists(conn, 'Goin'):
        learned = conn.execute("SELECT COUNT(*) FROM Goin WHERE status != 0").fetchone()[0]
        total = conn.execute("SELECT COUNT(*) FROM Goin").fetchone()[0]
        conn.execute("UPDATE Goin SET status = 0")
        print("  Goin: %d/%d 已学 -> 已重置" % (learned, total))

    # ============ 4. Count 表 ============
    conn.execute("UPDATE Count SET current = 0")
    print("  Count: 已重置 current=0（全部词库）")

    # ============ 5. StudyLog 表 ============
    deleted = conn.execute("SELECT COUNT(*) FROM StudyLog").fetchone()[0]
    conn.execute("DELETE FROM StudyLog")
    conn.execute("DELETE FROM sqlite_sequence WHERE name='StudyLog'")
    print("  StudyLog: %d 条记录 -> 已删除" % deleted)

    # ============ 6. EssayLog 表（2026-09-16 新增清洗，此前泄漏 AI 短文历史） ============
    if table_exists(conn, 'EssayLog'):
        deleted = conn.execute("SELECT COUNT(*) FROM EssayLog").fetchone()[0]
        conn.execute("DELETE FROM EssayLog")
        conn.execute("DELETE FROM sqlite_sequence WHERE name='EssayLog'")
        print("  EssayLog: %d 条 AI 短文历史 -> 已删除" % deleted)
    else:
        print("  EssayLog: 表不存在（程序启动时会自动创建），跳过")

    # ============ 7. Global 表（含 2026-09-16 新增的 pendingEssayWords） ============
    gcols = table_columns(conn, 'Global')
    gsets = [
        "currentWordNumber = 5",
        "currentBookName = 'CET6_3'",
        "autoPlay = 1",   # 与 Select.cs 的 AUTO_PLAY 默认值一致
        "EngType = 2",    # 与 ENG_TYPE 默认值一致
        "autoLog = 1",    # 与 AUTO_LOG 默认值一致
    ]
    if 'pendingEssayWords' in gcols:
        gsets.append("pendingEssayWords = ''")
    conn.execute("UPDATE Global SET " + ", ".join(gsets))
    print("  Global: 已重置为默认词库 CET6_3 / 每次 5 词 / 配置项恢复默认%s"
          % (" / AI 短文累积清空" if 'pendingEssayWords' in gcols else ""))

    # ============ 8. 下划线前缀的历史备份表（2026-09-16 新增清理） ============
    backups = [r[0] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name GLOB '_*'").fetchall()]
    for t in backups:
        n = conn.execute("SELECT COUNT(*) FROM [%s]" % t).fetchone()[0]
        conn.execute("DROP TABLE [%s]" % t)
        print("  %s: %d 行历史备份表 -> 已删除" % (t, n))
    if not backups:
        print("  历史备份表: 无")

    conn.commit()

    # ============ VACUUM（回收空间） ============
    print("  正在 VACUUM...")
    conn.execute("VACUUM")
    conn.close()

    # 删除备份（清洗成功）
    os.remove(bak_path)
    print("  备份已删除（清洗成功）")

    # 最终大小
    new_size = os.path.getsize(db_path)
    print("\nDone. DB size: %.1f MB" % (new_size / 1024 / 1024))


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("用法: python reset_database_for_distribution.py <db_path>")
        sys.exit(1)
    reset_database(sys.argv[1])
