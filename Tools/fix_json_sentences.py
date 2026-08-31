"""
清理数据库中 JSON 格式的 sentence 字段
"""
import sqlite3, json, os

DB_PATH = r"E:\ToastFish.v3.0\ToastFish\Resources\inami.db"

db = sqlite3.connect(DB_PATH)
c = db.cursor()

ENGLISH_TABLES = [
    'CET4_1','CET4_3','CET6_1','CET6_2','CET6_3',
    'Level4_1','Level4luan_2','Level8_1','Level8luan_2',
    'KaoYan_1','KaoYan_2','IELTS_3','TOEFL_2',
    'GRE_2','GMAT_3','SAT_2'
]

fixed = 0

for tbl in ENGLISH_TABLES:
    try:
        # Fix sentence field
        like1 = '%{%'
        c.execute('SELECT wordRank, headWord, sentence FROM [' + tbl + '] WHERE sentence LIKE ?', (like1,))
        for rank, word, sent in c.fetchall():
            if not sent or not sent.strip():
                continue
            s = sent.strip()
            if s.startswith('{') and 'COLLOINEXA' in s:
                try:
                    obj = json.loads(s)
                    arr = obj.get('COLLOINEXA', [])
                    if isinstance(arr, list) and len(arr) > 0:
                        new_sent = arr[0]
                        c.execute('UPDATE [' + tbl + '] SET sentence=? WHERE wordRank=?', (new_sent, rank))
                        fixed += 1
                        if fixed <= 20:
                            print(u'  [' + tbl + '] ' + word + ': JSON -> ' + new_sent[:80])
                    else:
                        c.execute('UPDATE [' + tbl + '] SET sentence=? WHERE wordRank=?', ('', rank))
                        fixed += 1
                except:
                    pass
            elif s.startswith('['):
                try:
                    arr = json.loads(s)
                    if isinstance(arr, list) and len(arr) > 0:
                        c.execute('UPDATE [' + tbl + '] SET sentence=? WHERE wordRank=?', (str(arr[0]), rank))
                        fixed += 1
                except:
                    pass

        # Fix phrase field with JSON
        c.execute('SELECT wordRank, headWord, phrase FROM [' + tbl + '] WHERE phrase LIKE ?', (like1,))
        for rank, word, ph in c.fetchall():
            if not ph or not ph.startswith('{'):
                continue
            try:
                obj = json.loads(ph)
                if 'COLLOINEXA' in obj:
                    c.execute('UPDATE [' + tbl + '] SET phrase=? WHERE wordRank=?', ('', rank))
                    fixed += 1
            except:
                pass
    except Exception as e:
        pass

db.commit()
db.close()
print('\nCleaned ' + str(fixed) + ' JSON records')
