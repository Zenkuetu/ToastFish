# -*- coding: utf-8 -*-
"""
将 netem_full_list.sql（2024 考研英语一大纲词汇表，按词频排序）导入 inami.db，
新建 KaoYan_3 词库表。

原始字段: id, frequency, word, definition, variant, category, subcategory
字段映射:
  id         -> wordRank  (主键)
  word       -> headWord
  definition -> tranCN
  frequency / variant / category / subcategory  -> 原样保留为独立列
新增学习必需字段:
  bookID, status, pos(空), difficulty, daysBetweenReviews, lastScore,
  dateLastReviewed, dateLastReviewed_bak

说明: 发音(usPhone/ukPhone/usSpeech/ukSpeech)、例句(sentence/sentenceCN)、
      词组(phrase/phraseCN)、复盘题(question/explain/rightIndex/examType/
      choiceIndexOne~Four) 均为可空字段，主程序已做判空兜底，故不建这些列。
      但 pos 列必须保留(仪表盘 generate_dashboard.py 硬编码 SELECT pos)。
"""
import re
import os
import sys
import io
import sqlite3

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

BASE = r'E:\ToastFish.v3.0\ToastFish'
SQL_FILE = os.path.join(BASE, 'Resources', 'netem_full_list.sql')
DB_FILE = os.path.join(BASE, 'Resources', 'inami.db')
TABLE = 'KaoYan_3'


def parse_insert(line):
    """解析一行 MySQL INSERT，返回 7 个字段的原始字符串列表，失败返回 None。"""
    m = re.match(r"INSERT INTO `netem_full_list` VALUES \((.*)\);?\s*$", line.strip())
    if not m:
        return None
    inner = m.group(1)

    fields = []
    cur = ''
    in_str = False
    i = 0
    n = len(inner)
    while i < n:
        c = inner[i]
        if not in_str:
            if c == "'":
                in_str = True
            elif c == ',':
                fields.append(cur.strip())
                cur = ''
            else:
                cur += c
        else:
            if c == "'":
                if i + 1 < n and inner[i + 1] == "'":  # SQL 转义 '' -> '
                    cur += "'"
                    i += 1
                else:
                    in_str = False
            else:
                cur += c
        i += 1
    fields.append(cur.strip())
    if len(fields) != 7:
        return None
    return fields


def to_value(s):
    """字符串字段值转 Python 值：NULL->None，整数->int，其余为去引号字符串。"""
    s = s.strip()
    if s == 'NULL':
        return None
    if re.match(r'^-?\d+$', s):
        return int(s)
    return s


def main():
    rows = []
    with open(SQL_FILE, encoding='utf-8') as f:
        for line in f:
            if not line.strip().startswith('INSERT'):
                continue
            fs = parse_insert(line)
            if fs is None:
                print('解析失败:', line[:120])
                continue
            rows.append(fs)

    print('解析到 %d 条记录' % len(rows))

    # 校验 id 连续 1..N
    ids = sorted(int(fs[0]) for fs in rows)
    assert ids == list(range(1, len(rows) + 1)), 'id 不连续，缺失/重复'

    conn = sqlite3.connect(DB_FILE)
    cur = conn.cursor()

    cur.execute('DROP TABLE IF EXISTS [%s]' % TABLE)
    cur.execute('''
        CREATE TABLE [%s](
            wordRank INTEGER PRIMARY KEY NOT NULL,
            bookID TEXT NOT NULL,
            status int NOT NULL DEFAULT 0,
            headWord TEXT NOT NULL,
            pos TEXT,
            tranCN TEXT NOT NULL,
            frequency INTEGER,
            variant TEXT,
            category TEXT,
            subcategory TEXT,
            difficulty REAL NOT NULL DEFAULT 0.3,
            daysBetweenReviews REAL NOT NULL DEFAULT 3,
            lastScore REAL NOT NULL DEFAULT 0,
            dateLastReviewed TEXT DEFAULT NULL,
            dateLastReviewed_bak TEXT DEFAULT NULL
        )
    ''' % TABLE)

    data = []
    for fs in rows:
        rid = int(fs[0])
        freq = to_value(fs[1])
        word = to_value(fs[2])
        definition = to_value(fs[3])
        variant = to_value(fs[4])
        category = to_value(fs[5])
        subcategory = to_value(fs[6])
        data.append((rid, word, definition, freq, variant, category, subcategory))

    cur.executemany('''
        INSERT INTO [%s]
        (wordRank, bookID, status, headWord, pos, tranCN, frequency, variant,
         category, subcategory, difficulty, daysBetweenReviews, lastScore,
         dateLastReviewed, dateLastReviewed_bak)
        VALUES (?, '%s', 0, ?, NULL, ?, ?, ?, ?, ?, 0.3, 3, 0, NULL, NULL)
    ''' % (TABLE, TABLE), data)

    cur.execute("DELETE FROM Count WHERE bookName = '%s'" % TABLE)
    cur.execute("INSERT INTO Count (bookName, number, current) VALUES ('%s', %d, 0)"
                % (TABLE, len(rows)))

    conn.commit()
    # 强制 checkpoint，把 WAL 写入合并回主文件，避免 cp 主文件时丢失未合并数据
    conn.execute('PRAGMA wal_checkpoint(TRUNCATE)')

    cnt = cur.execute('SELECT COUNT(*) FROM [%s]' % TABLE).fetchone()[0]
    print('KaoYan_3 记录数:', cnt)
    print('样例 3 行:')
    for r in cur.execute(
            'SELECT wordRank, headWord, tranCN, frequency, variant, category, subcategory '
            'FROM [%s] LIMIT 3' % TABLE):
        print('  ', r)
    print('o\'clock 行:', cur.execute(
        "SELECT wordRank, headWord FROM [%s] WHERE headWord LIKE \"o'%%\"" % TABLE).fetchall())
    print('variant 非空样本:', cur.execute(
        'SELECT headWord, variant FROM [%s] WHERE variant IS NOT NULL LIMIT 3' % TABLE).fetchall())
    print('Count 记录:', cur.execute(
        "SELECT * FROM Count WHERE bookName='%s'" % TABLE).fetchall())
    conn.close()
    print('导入完成')


if __name__ == '__main__':
    main()
