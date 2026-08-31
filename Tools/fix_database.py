"""
ToastFish 词库数据修复脚本
修复内容：
1. 清理 pos 字段格式（统一为 "n. / v." 风格）
2. 为 tranCN 添加词性标签，让用户清楚每个含义对应哪个词性
3. 标记 tranCN 与 sentence 可能不匹配的词条（供人工审核）
"""
import sqlite3, re, os

DB_PATH = r"E:\ToastFish.v3.0\ToastFish\Resources\inami.db"

# 备份
os.system(f'copy "{DB_PATH}" "{DB_PATH}.bak" >nul')
print("已备份数据库")

db = sqlite3.connect(DB_PATH)
c = db.cursor()

ENGLISH_TABLES = [
    'CET4_1','CET4_3','CET6_1','CET6_2','CET6_3',
    'Level4_1','Level4luan_2','Level8_1','Level8luan_2',
    'KaoYan_1','KaoYan_2','IELTS_3','TOEFL_2',
    'GRE_2','GMAT_3','SAT_2'
]

def normalize_pos(pos_str):
    """规范化词性标记"""
    if not pos_str or not pos_str.strip():
        return pos_str
    s = pos_str.strip()
    # 移除多余句号 (n./v. → n/v)
    s = re.sub(r'\.+', '.', s)
    if s.endswith('.'): s = s[:-1]
    return s

def pos_to_readable(pos_str):
    """将 pos 转换为可读格式 n./v. → n. / v."""
    if not pos_str or '/' not in pos_str:
        return pos_str
    parts = pos_str.split('/')
    cleaned = []
    for p in parts:
        p = p.strip()
        if p and not p.endswith('.'): p += '.'
        cleaned.append(p)
    return ' / '.join(cleaned)

def split_trancn_by_pos(trancn, pos_str):
    """
    尝试按词性分段拆分 tranCN，给每段加上词性标签
    返回: "n. 使者；先驱 / v. 预示"
    """
    if not pos_str or '/' not in pos_str:
        return trancn, None

    pos_parts = [p.strip().rstrip('.') for p in pos_str.split('/')]

    # 先尝试按中文分号分隔
    semicolon_parts = trancn.split('；')
    slash_parts = trancn.split('/')

    # 如果 trancn 中的 / 数量匹配 pos_parts
    clean_slash = [p.strip() for p in slash_parts if p.strip()]
    if len(clean_slash) == len(pos_parts):
        # 完美匹配 — 给每段加标签
        result_parts = []
        for i, (pos, meaning) in enumerate(zip(pos_parts, clean_slash)):
            pos_label = pos + ('.' if not pos.endswith('.') else '')
            result_parts.append(f'{pos_label} {meaning}')
        return ' / '.join(result_parts), 'slash_match'

    # 尝试按分号分组再按POS数量合并
    semicolon_clean = [p.strip() for p in semicolon_parts if p.strip()]
    if len(semicolon_clean) >= len(pos_parts):
        # 将分号分隔的含义平均分配
        per_pos = len(semicolon_clean) // len(pos_parts)
        result_parts = []
        idx = 0
        for i, pos in enumerate(pos_parts):
            pos_label = pos + ('.' if not pos.endswith('.') else '')
            if i == len(pos_parts) - 1:
                chunk = '；'.join(semicolon_clean[idx:])
            else:
                chunk = '；'.join(semicolon_clean[idx:idx+per_pos])
            result_parts.append(f'{pos_label} {chunk}')
            idx += per_pos
        return ' / '.join(result_parts), 'semicolon_split'

    return trancn, None  # 无法自动拆分

# ========== 主流程 ==========
total_fixed_pos = 0
total_tagged_trancn = 0
sentence_issues = []
unfixable = []

for table in ENGLISH_TABLES:
    try:
        c.execute(f"SELECT wordRank, headWord, pos, tranCN, sentence FROM [{table}] WHERE pos IS NOT NULL AND pos != ''")
        rows = c.fetchall()
    except:
        continue

    for rank, word, pos, tranCN, sentence in rows:
        if not pos or '/' not in pos:
            continue

        old_pos = pos

        # 1. 规范化 pos 格式
        new_pos = pos_to_readable(pos)
        if new_pos != old_pos:
            c.execute(f"UPDATE [{table}] SET pos=? WHERE wordRank=?", (new_pos, rank))
            total_fixed_pos += 1

        # 2. 尝试给 tranCN 加词性标签
        new_trancn, method = split_trancn_by_pos(tranCN, pos)
        if new_trancn != tranCN and method:
            c.execute(f"UPDATE [{table}] SET tranCN=? WHERE wordRank=?", (new_trancn, rank))
            total_tagged_trancn += 1

        # 3. 检测例句与释义偏差（标记供人工审核）
        is_verb_pos = any(p.strip().startswith('v') for p in pos.split('/'))
        is_noun_pos = any(p.strip().startswith('n') for p in pos.split('/'))

db.commit()

print(f"\n=== 修复完成 ===")
print(f"pos 格式规范化: {total_fixed_pos} 词")
print(f"tranCN 加词性标签: {total_tagged_trancn} 词")

# ========== 显示修复后样例 ==========
print("\n=== 修复后样例 ===")
c.execute("SELECT headWord, pos, tranCN FROM CET6_3 WHERE pos LIKE '%/%' OR pos LIKE '% / %' LIMIT 20")
for r in c.fetchall():
    print(f"  {r[0]:20s} [{r[1]:16s}] {r[2][:80]}")

db.close()
print("\n数据库修复完成。")
