"""
数据质量修复：人工审核并修复之前标记的问题
1. pistol 等词：例句为动词用法但 pos 缺 v → 补充词性和释义
2. phraseCN 中 [] 括号标注清理
3. underneath 等词组翻译修正
"""
import sqlite3

DB_PATH = r"E:\ToastFish.v3.0\ToastFish\Resources\inami.db"
db = sqlite3.connect(DB_PATH)
c = db.cursor()

ENGLISH_TABLES = [
    'CET4_1','CET4_3','CET6_1','CET6_2','CET6_3',
    'Level4_1','Level4luan_2','Level8_1','Level8luan_2',
    'KaoYan_1','KaoYan_2','IELTS_3','TOEFL_2',
    'GRE_2','GMAT_3','SAT_2'
]

# ======= 1. Fix words where sentence uses word as verb but pos has no v =======
verb_fixes = {
    'pistol': {
        'pos': 'n. / v.',
        'tranCN': 'n. 手枪 / v. 用手枪威胁；用手枪射击',
    },
}

for word, fix in verb_fixes.items():
    for tbl in ENGLISH_TABLES:
        try:
            c.execute('UPDATE [' + tbl + '] SET pos=?, tranCN=? WHERE headWord=?',
                      (fix['pos'], fix['tranCN'], word))
            if c.rowcount > 0:
                print('Fixed: [' + tbl + '] ' + word)
        except:
            pass

# ======= 2. Clean phraseCN bracket annotations =======
# chap: '[英式英语][用于称呼男性朋友]老兄，伙计，家伙[相当于 old boy， old fellow]'
# single: '[口语][用于强调]每一个'
bracket_fixes = {
    'old chap': {
        'old': 'old chap',
        'new': '老兄，伙计，家伙（英式用语，相当于 old boy / old fellow）'
    },
    'every single': {
        'old': '[口语][用于强调]每一个',
        'new': '每一个（用于强调，口语）'
    },
}

for tbl in ENGLISH_TABLES:
    for key, fix in bracket_fixes.items():
        try:
            c.execute('UPDATE [' + tbl + '] SET phraseCN=? WHERE phrase=? AND phraseCN=?',
                      (fix['new'], fix['old'], 'x_skip'))
            c.execute('UPDATE [' + tbl + '] SET phraseCN=? WHERE phrase=? AND phraseCN LIKE ?',
                      (fix['new'], key, '%[%]%'))
            if c.rowcount > 0:
                print('Cleaned phraseCN: [' + tbl + '] ' + key)
        except:
            pass

# ======= 3. Fix underneath phraseCN =======
# phrase: 'underneath the surface / hidden underneath / underneath and below'
# phraseCN: '在表面之下 / 隐蔽在下面 / 在底面底下'
# Fix: '在底面底下' → '在下方；在...底下'
c.execute('SELECT wordRank, phrase, phraseCN FROM CET6_3 WHERE headWord=?', ('underneath',))
row = c.fetchone()
if row:
    rank, phrase, phraseCN = row
    parts = phraseCN.split(' / ')
    if len(parts) >= 3 and '底面底下' in parts[2]:
        parts[2] = '在下方；在...底下'
        new_phraseCN = ' / '.join(parts)
        c.execute('UPDATE CET6_3 SET phraseCN=? WHERE wordRank=?', (new_phraseCN, rank))
        print('Fixed underneath phraseCN: ' + phraseCN + ' -> ' + new_phraseCN)

db.commit()
db.close()
print('\nDone.')
