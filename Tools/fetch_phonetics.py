"""
批量从有道词典 API 获取音标，更新 inami.db 中 usphone/ukphone 为空的词条
用法：python fetch_phonetics.py
"""
import sqlite3
import json
import time
import sys
import urllib.request

DB_PATH = r"E:\ToastFish.v3.0\ToastFish\Resources\inami.db"
REQUEST_DELAY = 0.3  # 每次请求间隔（秒）

TABLES_TO_FIX = ["CET6_2", "CET6_3"]


def fetch_phonetic(word):
    """从有道 API 获取音标，返回 (usphone, ukphone)"""
    try:
        url = f"https://dict.youdao.com/jsonapi?q={word}&le=eng"
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        resp = urllib.request.urlopen(req, timeout=15)
        data = json.loads(resp.read())

        us, uk = "", ""
        # 优先从 ec 节点取
        ec_words = data.get("ec", {}).get("word", [])
        if ec_words:
            us = ec_words[0].get("usphone", "")
            uk = ec_words[0].get("ukphone", "")

        # ec 无数据则尝试 simple 节点
        if not us and not uk:
            simple_words = data.get("simple", {}).get("word", [])
            if simple_words:
                us = simple_words[0].get("usphone", "")
                uk = simple_words[0].get("ukphone", "")

        return (us or "", uk or "")
    except Exception as e:
        print(f"  [!] API 请求失败: {e}")
        return ("", "")


def main():
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row

    total_updated = 0
    total_failed = 0

    for table in TABLES_TO_FIX:
        # 找出该表所有 usphone 为空的词
        rows = conn.execute(
            f"SELECT wordRank, headWord FROM [{table}] WHERE usphone IS NULL OR usphone = ''"
        ).fetchall()

        if not rows:
            print(f"[{table}] 无需更新")
            continue

        n = len(rows)
        print(f"[{table}] {n} 条待更新")

        updated = 0
        failed = 0
        for i, row in enumerate(rows):
            rank = row["wordRank"]
            word = row["headWord"]

            # 跳过纯数字/特殊值
            if word is None or word.strip() == "":
                continue

            us, uk = fetch_phonetic(word)
            time.sleep(REQUEST_DELAY)

            if us or uk:
                conn.execute(
                    f"UPDATE [{table}] SET usphone = ?, ukphone = ? WHERE wordRank = ?",
                    (us, uk, rank),
                )
                updated += 1
            else:
                failed += 1
                print(f"  [{i+1}/{n}] {word} (rank={rank}) -> 无音标数据")

            if (i + 1) % 50 == 0 or (i + 1) == n:
                conn.commit()
                print(
                    f"  [{table}] 进度 {i+1}/{n} | 已更新 {updated} | 失败 {failed}"
                )

        total_updated += updated
        total_failed += failed
        conn.commit()

    conn.close()
    print(f"\n===== 完成 =====")
    print(f"总更新: {total_updated}, 总失败: {total_failed}")
    if total_failed > 0:
        print("失败词条保存了空字符串，请手动检查")


if __name__ == "__main__":
    main()
