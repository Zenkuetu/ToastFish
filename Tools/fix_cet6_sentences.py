# -*- coding: utf-8 -*-
"""修复 CET6_3 例句错位/损坏数据（2026-07-17），备份: inami.db.sentence_fix_bak"""
import sqlite3, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

DB = r'E:\ToastFish.v3.0\ToastFish\Resources\inami.db'

# (wordRank, sentence, sentenceCN)  sentenceCN=None 表示保留原中文
FIXES = [
(1031, "I can't say with any certainty when he will arrive.", "我无法确切地说他什么时候到。"),
(1059, "She bears a striking resemblance to her mother.", "她和她母亲长得非常像。"),
(1061, "Women constitute about 40% of the country's workforce.", "女性约占该国劳动力总数的40%。"),
(1574, "The city is experiencing an economic boom in the tourism industry.", "这座城市的旅游业正经历一场经济繁荣。"),
(1650, "They finally settled the dispute out of court.", "他们最终在庭外解决了纠纷。"),
(1772, "He attended a preparatory course before entering university.", "他在进入大学前上了预科课程。"),
(1837, "The government's new policy has drawn sharp criticism from the public.", "政府的新政策招致了公众的尖锐批评。"),
(1932, "The white walls make a striking contrast with the dark floor.", "白色的墙壁与深色的地板形成鲜明的对比。"),
(1936, "The church's doctrine teaches compassion and forgiveness.", "教会的教义教导人们要有同情心和宽恕之心。"),
(2034, "They lived a life of luxury in a huge mansion.", "他们住在巨大的宅邸里，过着奢华的生活。"),
(2050, "She sat quietly, pondering the meaning of his words.", "她静静地坐着，思考他话中的含义。"),
]
FIXES += [
(2054, "The author builds up a useful composite picture of contemporary consumer culture.", None),
(2124, "The frequency of earthquakes in this region has increased in recent years.", "近年来该地区地震的频率有所增加。"),
(2155, "Scientists have made a major breakthrough in cancer research.", "科学家在癌症研究方面取得了重大突破。"),
(2180, "She has two children from her previous marriage.", "她与前一段婚姻育有两个孩子。"),
(2809, "The odds are that he will commit the same crime again.", None),
(3022, "He is a well-known writer whose books are read all over the world.", "他是一位知名作家，他的书畅销世界各地。"),
(3551, "Luckily, I have a very understanding boss.", None),
(3633, "Biotechnology has revolutionized the development of new medicines.", "生物技术彻底改变了新药的研发。"),
(3663, "There is a certain logic in their choice of architect.", None),
(4119, "After a lifetime of poverty, his last few years were spent in comparative comfort.", None),
]

conn = sqlite3.connect(DB)
cur = conn.cursor()
for rank, sent, cn in FIXES:
    hw = cur.execute("SELECT headWord FROM CET6_3 WHERE wordRank=?", (rank,)).fetchone()
    if not hw:
        print('MISSING wordRank=%d' % rank); continue
    if cn is None:
        cur.execute("UPDATE CET6_3 SET sentence=? WHERE wordRank=?", (sent, rank))
    else:
        cur.execute("UPDATE CET6_3 SET sentence=?, sentenceCN=? WHERE wordRank=?", (sent, cn, rank))
    print('FIXED %d %s' % (rank, hw[0]))
# 顺带清理全表软连字符 U+00AD
n = cur.execute("SELECT COUNT(*) FROM CET6_3 WHERE sentence LIKE '%'||CHAR(173)||'%'").fetchone()[0]
cur.execute("UPDATE CET6_3 SET sentence = REPLACE(sentence, CHAR(173), '') WHERE sentence LIKE '%'||CHAR(173)||'%'")
print('soft-hyphen cleaned rows: %d' % n)
conn.commit()
print('integrity:', conn.execute('PRAGMA integrity_check').fetchone()[0])
conn.close()
