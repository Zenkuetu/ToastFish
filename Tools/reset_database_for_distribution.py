"""
数据库分发清洗脚本
将 inami.db 中所有学习数据重置为初始状态，保留完整词汇数据。
仅修改 staging 目录下的副本，绝不触碰真实数据库。

用法:
    python reset_database_for_distribution.py <staging_db_path>
    例: python reset_database_for_distribution.py E:\ToastFish.v3.0\Installer\staging\Resources\inami.db
"""
import sqlite3
import sys
import os
import shutil

def reset_database(db_path):
    if not os.path.exists(db_path):
        print(f"错误: 文件不存在 {db_path}")
        sys.exit(1)

    # 备份（安全起见）
    bak_path = db_path + ".before_reset_bak"
    shutil.copy2(db_path, bak_path)
    print(f"备份已保存: {bak_path}")

    conn = sqlite3.connect(db_path)
    conn.execute("PRAGMA journal_mode=OFF")  # 加速批量写入

    # ============ 英语词表（16 张，结构相同） ============
    eng_tables = [
        'CET4_1', 'CET4_3', 'CET6_1', 'CET6_2', 'CET6_3',
        'GMAT_3', 'GRE_2', 'IELTS_3', 'SAT_2', 'TOEFL_2',
        'KaoYan_1', 'KaoYan_2', 'Level4_1', 'Level4luan_2',
        'Level8_1', 'Level8luan_2'
    ]

    for table in eng_tables:
        # 检查表是否存在
        exists = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name=?",
            (table,)
        ).fetchone()
        if not exists:
            print(f"  跳过: {table} (不存在)")
            continue

        learned = conn.execute(f"SELECT COUNT(*) FROM [{table}] WHERE status != 0").fetchone()[0]
        total = conn.execute(f"SELECT COUNT(*) FROM [{table}]").fetchone()[0]

        conn.execute(f"""
            UPDATE [{table}] SET
                status = 0,
                difficulty = 0.3,
                daysBetweenReviews = 3,
                lastScore = 0,
                dateLastReviewed = NULL,
                dateLastReviewed_bak = NULL,
                question = NULL,
                explain = NULL,
                rightIndex = NULL,
                examType = NULL,
                choiceIndexOne = NULL,
                choiceIndexTwo = NULL,
                choiceIndexThree = NULL,
                choiceIndexFour = NULL
        """)
        print(f"  {table}: {learned}/{total} 已学 → 已重置")

    # ============ KaoYan_3（精简结构：无 question/choiceIndex 等复盘题列，单独清洗） ============
    kt = 'KaoYan_3'
    kt_exists = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name=?", (kt,)
    ).fetchone()
    if kt_exists:
        kt_learned = conn.execute(f"SELECT COUNT(*) FROM [{kt}] WHERE status != 0").fetchone()[0]
        kt_total = conn.execute(f"SELECT COUNT(*) FROM [{kt}]").fetchone()[0]
        conn.execute(f"""
            UPDATE [{kt}] SET
                status = 0,
                difficulty = 0.3,
                daysBetweenReviews = 3,
                lastScore = 0,
                dateLastReviewed = NULL,
                dateLastReviewed_bak = NULL
        """)
        print(f"  {kt}: {kt_learned}/{kt_total} 已学 → 已重置")
    else:
        print(f"  跳过: {kt} (不存在)")

    # ============ 日语词表 StdJp_Mid ============
    learned = conn.execute("SELECT COUNT(*) FROM StdJp_Mid WHERE status != 0").fetchone()[0]
    total = conn.execute("SELECT COUNT(*) FROM StdJp_Mid").fetchone()[0]
    conn.execute("UPDATE StdJp_Mid SET status = 0, phone = NULL")
    print(f"  StdJp_Mid: {learned}/{total} 已学 → 已重置")

    # ============ 五十音 Goin ============
    learned = conn.execute("SELECT COUNT(*) FROM Goin WHERE status != 0").fetchone()[0]
    total = conn.execute("SELECT COUNT(*) FROM Goin").fetchone()[0]
    conn.execute("UPDATE Goin SET status = 0")
    print(f"  Goin: {learned}/{total} 已学 → 已重置")

    # ============ Count 表 ============
    conn.execute("UPDATE Count SET current = 0")
    print(f"  Count: 已重置 current=0（全部词库）")

    # ============ StudyLog 表 ============
    deleted = conn.execute("SELECT COUNT(*) FROM StudyLog").fetchone()[0]
    conn.execute("DELETE FROM StudyLog")
    # 重置自增 ID
    conn.execute("DELETE FROM sqlite_sequence WHERE name='StudyLog'")
    print(f"  StudyLog: {deleted} 条记录 → 已删除")

    # ============ Global 表 ============
    conn.execute("""
        UPDATE Global SET
            currentWordNumber = 5,
            currentBookName = 'CET6_3'
    """)
    print(f"  Global: 已重置为默认词库 CET6_3, 每次 5 词")

    conn.commit()

    # ============ VACUUM（回收空间） ============
    print("  正在 VACUUM...")
    conn.execute("VACUUM")
    conn.close()

    # 删除备份（清洗成功）
    os.remove(bak_path)
    print(f"  备份已删除（清洗成功）")

    # 最终大小
    new_size = os.path.getsize(db_path)
    print(f"\nDone. DB size: {new_size / 1024 / 1024:.1f} MB")

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("用法: python reset_database_for_distribution.py <db_path>")
        sys.exit(1)
    reset_database(sys.argv[1])
