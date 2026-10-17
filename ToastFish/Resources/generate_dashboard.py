#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ToastFish Dashboard Generator
读取 inami.db 学习数据，生成 Cyber Anime 风格 Vue 3 可视化仪表盘 HTML。
输出: ../ToastFish/Resources/dashboard.html
"""

import sqlite3
import json
import math
import os
import sys
import socket
import subprocess
from datetime import datetime, timedelta
from collections import defaultdict

# ============================================================
# 配置
# ============================================================

# 自适应：脚本可能位于 Tools/ 或 Resources/，以 inami.db 位置为基准
_SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if os.path.basename(_SCRIPT_DIR) == 'Resources':
    _RES_DIR = _SCRIPT_DIR
else:
    _RES_DIR = os.path.join(_SCRIPT_DIR, "..", "ToastFish", "Resources")
DB_PATH = os.path.join(_RES_DIR, "inami.db")
OUTPUT_PATH = os.path.join(_RES_DIR, "dashboard.html")

# 需要统计的英语词库表（排除系统表和日语表，以及下划线前缀的历史备份表如 _CET6_3_old_20260810）
EXCLUDE_TABLES = {'Count', 'Global', 'StudyLog', 'Goin', 'StdJp_Mid', 'sqlite_sequence'}

# ============================================================
# 数据读取
# ============================================================

def get_db():
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn


def read_all_data(conn):
    """一次性读取所有需要的数据"""
    # StudyLog
    studylog = [dict(r) for r in conn.execute(
        'SELECT * FROM StudyLog ORDER BY date'
    ).fetchall()]

    # Global 配置
    global_row = conn.execute('SELECT * FROM Global').fetchone()
    global_config = dict(global_row) if global_row else {}

    # Count 表
    counts = {r['bookName']: {'number': r['number'], 'current': r['current']}
              for r in conn.execute('SELECT * FROM Count').fetchall()}

    # 所有词库的统计
    all_books = [r[0] for r in conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table'"
    ).fetchall()]
    book_stats = {}
    for t in all_books:
        if t in EXCLUDE_TABLES or t.startswith('_'):
            continue
        try:
            total = conn.execute(f'SELECT COUNT(*) FROM [{t}]').fetchone()[0]
            learned = conn.execute(f'SELECT COUNT(*) FROM [{t}] WHERE status != 0').fetchone()[0]
            mastered = conn.execute(f'SELECT COUNT(*) FROM [{t}] WHERE status = 5').fetchone()[0]
            if total > 0:
                book_stats[t] = {'total': total, 'learned': learned, 'mastered': mastered}
        except Exception:
            pass

    # 当前词库的已学词 SM2 参数
    current_book = global_config.get('currentBookName', 'CET6_3')
    words = []
    try:
        words = [dict(r) for r in conn.execute(
            f'SELECT wordRank, headWord, pos, tranCN, difficulty, daysBetweenReviews, '
            f'lastScore, status, dateLastReviewed '
            f'FROM [{current_book}] WHERE status != 0'
        ).fetchall()]
    except Exception:
        pass

    # 全部词库的已学词（按 headWord 去重）—— 学术页跨词库统计用
    all_words = read_all_learned_words(conn, all_books)

    return studylog, global_config, counts, words, all_words, book_stats, current_book


SM2_REQUIRED_COLUMNS = {'headWord', 'difficulty', 'daysBetweenReviews', 'lastScore', 'status'}


def read_all_learned_words(conn, tables):
    """汇总所有词库的已学词，按 headWord 去重。

    学术页原先只看 currentBookName，切换词库后整页数字全变、书与书之间不可比。
    这里合并全部词库；同一单词被多个词库收录时，保留学习程度更深
    （status 更高、难度更大）的那一条。
    """
    best = {}
    for t in tables:
        if t in EXCLUDE_TABLES or t.startswith('_'):
            continue
        try:
            cols = {r[1] for r in conn.execute(f'PRAGMA table_info([{t}])')}
            if not SM2_REQUIRED_COLUMNS <= cols:
                continue
            rank_expr = 'wordRank' if 'wordRank' in cols else '0 AS wordRank'
            pos_expr = 'pos' if 'pos' in cols else "'' AS pos"
            tran_expr = 'tranCN' if 'tranCN' in cols else "'' AS tranCN"
            rows = conn.execute(
                f'SELECT {rank_expr}, headWord, {pos_expr}, {tran_expr}, difficulty, '
                f'daysBetweenReviews, lastScore, status FROM [{t}] WHERE status != 0'
            ).fetchall()
        except Exception:
            continue
        for r in rows:
            w = dict(r)
            w['book'] = t
            cur = best.get(w['headWord'])
            if cur is None or (w['status'], w['difficulty']) > (cur['status'], cur['difficulty']):
                best[w['headWord']] = w
    return list(best.values())


def read_essay_logs(conn):
    """读取 AI 短文历史记录（2026-07-22）。"""
    try:
        conn.row_factory = sqlite3.Row
        rows = conn.execute(
            'SELECT * FROM EssayLog ORDER BY createdAt DESC'
        ).fetchall()
        logs = []
        for r in rows:
            log = dict(r)
            # JSON 字段还原为 Python 对象
            try:
                log['words'] = json.loads(log['words'])
            except: pass
            try:
                log['questions'] = json.loads(log['questions'])
            except: pass
            logs.append(log)
        conn.row_factory = None
        return logs
    except Exception:
        conn.row_factory = None
        return []


# ============================================================
# 指标计算 — 页面 1（学习概览）
# ============================================================

BOOK_NAMES = {
    'CET4_1': '四级核心词汇', 'CET4_3': '四级完整词汇',
    'CET6_1': '六级核心词汇', 'CET6_2': '六级新增词汇', 'CET6_3': '六级完整词汇',
    'Level4_1': '专四真题高频词', 'Level4luan_2': '专四核心词汇',
    'Level8_1': '专八真题高频词', 'Level8luan_2': '专八核心词汇',
    'KaoYan_1': '考研必考词汇', 'KaoYan_2': '考研完整词汇', 'KaoYan_3': '考研词频词汇',
    'GMAT_3': 'GMAT 词汇', 'GRE_2': 'GRE 词汇', 'IELTS_3': 'IELTS 词汇',
    'TOEFL_2': 'TOEFL 词汇', 'SAT_2': 'SAT 词汇',
    'Goin': '顺序五十音', 'StdJp_Mid': '标准日本语中级词汇',
}


def compute_page1(studylog, all_words, counts, book_stats, current_book):
    """学习概览页指标。

    all_words 是「全部词库的已学词」（按 headWord 去重）。难度/间隔分布与学术页
    同口径，避免同一份报告里两个页面的总数对不上。
    """
    # --- KPI ---
    total_reviewed = sum(r['wordsReviewed'] for r in studylog)
    total_easy = sum(r['scoreEASY'] for r in studylog)
    total_good = sum(r['scoreGOOD'] for r in studylog)
    total_hard = sum(r['scoreHARD'] for r in studylog)
    total_again = sum(r['scoreAGAIN'] for r in studylog)
    total_scored = total_easy + total_good + total_hard + total_again
    accuracy = (total_easy + total_good) / total_scored * 100 if total_scored > 0 else 0
    max_daily = max((r['wordsReviewed'] for r in studylog), default=0)

    # 连续天数：max_streak = 历史最长；current_streak = 最后一段（且未断，最后记录距今 <= 1 天）
    dates = sorted(set(r['date'] for r in studylog))
    today = datetime.now().date()
    max_streak = 0
    tail_streak = 0
    prev = None
    for ds in dates:
        d = datetime.strptime(ds, '%Y-%m-%d').date()
        tail_streak = tail_streak + 1 if (prev is not None and (d - prev).days == 1) else 1
        max_streak = max(max_streak, tail_streak)
        prev = d
    current_streak = 0
    if dates and (today - prev).days <= 1:
        current_streak = tail_streak
    total_days = len(dates)

    # 当前词库进度：用词表现算的 learned（Count.current 是缓存列，可能漂移）
    cur_stats = book_stats.get(current_book, {})
    book_total = cur_stats.get('total') or counts.get(current_book, {}).get('number', 0)
    book_current = cur_stats.get('learned') or counts.get(current_book, {}).get('current', 0)

    kpis = [
        {'icon': '📝', 'value': total_reviewed, 'label': '累计学习词次', 'format': 'int'},
        {'icon': '📖', 'value': len(all_words), 'label': '已学单词数（全库去重）', 'format': 'int'},
        {'icon': '🔥', 'value': current_streak, 'label': '当前连续天数', 'format': 'int'},
        {'icon': '🏅', 'value': max_streak, 'label': '最长连续天数', 'format': 'int'},
        {'icon': '📅', 'value': total_days, 'label': '有效学习天数', 'format': 'int'},
        {'icon': '🎯', 'value': round(accuracy, 1), 'label': '总正确率 %', 'format': 'pct'},
        {'icon': '⚡', 'value': max_daily, 'label': '最高单日词数', 'format': 'int'},
        {'icon': '📚', 'value': round(book_current / book_total * 100, 1) if book_total > 0 else 0,
         'label': f"{BOOK_NAMES.get(current_book, current_book)} 进度 %", 'format': 'pct'},
    ]

    # --- 每日趋势 / 累计（补全缺失日期，x 轴才是等距时间轴） ---
    by_date = {r['date']: r for r in studylog}
    daily_trend = []
    cumulative = []
    acc = 0
    if dates:
        d = datetime.strptime(dates[0], '%Y-%m-%d').date()
        last = datetime.strptime(dates[-1], '%Y-%m-%d').date()
        while d <= last:
            ds = d.strftime('%Y-%m-%d')
            r = by_date.get(ds)
            w = r['wordsReviewed'] if r else 0
            e = r['scoreEASY'] if r else 0
            g = r['scoreGOOD'] if r else 0
            h = r['scoreHARD'] if r else 0
            a = r['scoreAGAIN'] if r else 0
            acc += w
            daily_trend.append({
                'date': ds,
                'words': w, 'easy': e, 'good': g, 'hard': h, 'again': a,
                # 当天没学 → 不画点（0% 会被误读成"全错"）
                'accuracy': round((e + g) / w * 100, 1) if w > 0 else None,
            })
            cumulative.append({'date': ds, 'cumulative': acc})
            d += timedelta(days=1)

    # --- 学习日历热力图（最近 20 周，周一开头，未来日期单独标记） ---
    end_sunday = today + timedelta(days=(6 - today.weekday()))
    start_date = end_sunday - timedelta(days=20 * 7 - 1)
    date_word_map = {r['date']: r['wordsReviewed'] for r in studylog}
    calendar_data = []
    d = start_date
    while d <= end_sunday:
        ds = d.strftime('%Y-%m-%d')
        calendar_data.append({
            'date': ds,
            'count': date_word_map.get(ds, 0),
            'weekday': d.weekday(),
            'week': (d - start_date).days // 7,
            'future': d > today,
        })
        d += timedelta(days=1)

    # --- 难度分布（10 档，合意区按"与 0.15-0.35 有重叠"判定，与学术页一致） ---
    diff_hist = []
    for i in range(10):
        lo = round(i * 0.1, 1)
        hi = round(lo + 0.1, 1)
        if i == 9:
            cnt = sum(1 for w in all_words if w['difficulty'] >= lo)
        else:
            cnt = sum(1 for w in all_words if lo <= w['difficulty'] < hi)
        diff_hist.append({
            'label': f'{lo:.1f}-{hi:.1f}',
            'count': cnt,
            'desirable': lo < 0.35 and hi > 0.15,
        })

    # --- 间隔分布 ---
    interval_buckets = [
        {'label': '<1天', 'lo': 0, 'hi': 1},
        {'label': '1-3天', 'lo': 1, 'hi': 3},
        {'label': '3-7天', 'lo': 3, 'hi': 7},
        {'label': '1-2周', 'lo': 7, 'hi': 14},
        {'label': '2-4周', 'lo': 14, 'hi': 30},
        {'label': '1-2月', 'lo': 30, 'hi': 60},
        {'label': '>2月', 'lo': 60, 'hi': 9999},
    ]
    interval_hist = []
    for b in interval_buckets:
        if b['hi'] >= 9999:
            cnt = sum(1 for w in all_words if w['daysBetweenReviews'] >= b['lo'])
        else:
            cnt = sum(1 for w in all_words if b['lo'] <= w['daysBetweenReviews'] < b['hi'])
        interval_hist.append({'label': b['label'], 'count': cnt})

    # --- 词库进度排行（含中文名；0 进度的词库只计数，不逐个列出） ---
    book_progress = []
    for name, stats in sorted(book_stats.items(),
                              key=lambda x: x[1]['learned'], reverse=True):
        if stats['total'] > 0:
            book_progress.append({
                'name': name,
                'display': BOOK_NAMES.get(name, name),
                'total': stats['total'],
                'learned': stats['learned'],
                'mastered': stats['mastered'],
                'pct': round(stats['learned'] / stats['total'] * 100, 1),
            })
    started_books = sum(1 for b in book_progress if b['learned'] > 0)

    return {
        'kpis': kpis,
        'dailyTrend': daily_trend,
        'cumulative': cumulative,
        'calendarData': calendar_data,
        'diffHist': diff_hist,
        'intervalHist': interval_hist,
        'bookProgress': book_progress,
        'totalReviewed': total_reviewed,
        'totalWords': len(all_words),
        'startedBooks': started_books,
        'totalBooks': len(book_progress),
        'totalEasy': total_easy,
        'totalGood': total_good,
        'totalHard': total_hard,
        'totalAgain': total_again,
    }


# ============================================================
# 指标计算 — 页面 2（学术分析）
# ============================================================

def compute_page2(studylog, words):
    """学术分析页指标。

    words 是「全部词库的已学词」（按 headWord 去重），与当前选中词库无关，
    避免切换词库后整页数据变样。
    """
    if not words:
        return _empty_page2()

    # --- 复习结果 × 间隔档 ---
    # 注意：库里只保存每个词"最近一次"的评分，没有逐次复习历史；而间隔是"答对才扩张"，
    # 所以长间隔样本天然高正确率（生存偏差），真实的 Ebbinghaus 衰减率在这里不可测。
    # 能如实呈现的是：每个间隔档里最近一次评分的分布。
    forgetting_buckets = [
        ('<1天', 0, 1), ('1-3天', 1, 3), ('3-7天', 3, 7),
        ('1-2周', 7, 14), ('2-4周', 14, 30), ('1-2月', 30, 60), ('>2月', 60, float('inf')),
    ]
    forgetting_data = []
    for label, lo, hi in forgetting_buckets:
        bucket = [w for w in words if lo <= w['daysBetweenReviews'] < hi]
        n = len(bucket)
        again = sum(1 for w in bucket if w['lastScore'] <= 0.5)
        hard = sum(1 for w in bucket if 0.5 < w['lastScore'] < 0.8)
        good = sum(1 for w in bucket if w['lastScore'] == 0.8)
        easy = sum(1 for w in bucket if w['lastScore'] > 0.8)
        forgetting_data.append({
            'label': label, 'count': n,
            'again': again, 'hard': hard, 'good': good, 'easy': easy,
            # 与 C# 端 SM2+ Parameters.Correct=0.8 一致：Good / Easy 才算通过
            'passRate': round((good + easy) / n * 100, 1) if n else None,
            'failRate': round((again + hard) / n * 100, 1) if n else None,
        })

    # --- Bjork 双维模型 ---
    # 存储强度用 SM2 的复习间隔本身做代理（间隔就是稳定性估计）。
    # 旧公式 storage = -t / ln(min(score, 0.99)) 在 lastScore=1.0 时等于 99.5·t，
    # 只要有 2 天间隔就撞上 200 的上限，70% 的点堆成一条竖线。
    bjork_points = []
    for w in words:
        interval = w['daysBetweenReviews']
        if interval <= 0:
            continue
        bjork_points.append({
            'word': w['headWord'],
            'interval': round(interval, 1),
            'score': round(w['lastScore'], 2),
            'status': w['status'],
            'difficulty': round(w['difficulty'], 4),
        })

    # --- 难度分布 + 合意区 ---
    # 边界须 round：i*0.1 在 i=3 时是 0.30000000000000004，会把 difficulty 恰好=0.3 的词错分到低桶
    diff_buckets_detailed = []
    for i in range(10):
        lo = round(i * 0.1, 1)
        hi = round((i + 1) * 0.1, 1)
        if i == 9:
            # 末桶闭合：difficulty 恰为 1.0（或异常越界）也计入，不能再被静默丢弃
            cnt = sum(1 for w in words if w['difficulty'] >= lo)
        else:
            cnt = sum(1 for w in words if lo <= w['difficulty'] < hi)
        # 合意区按"桶与 [0.15, 0.35) 有重叠"判定；用桶中点会让 0.3-0.4 桶
        # （大量词恰好落在 0.3）漏掉高亮，看起来像"我的词都不在合意区"
        desirable = lo < 0.35 and hi > 0.15
        diff_buckets_detailed.append({
            'label': f'{lo:.1f}', 'lo': lo, 'hi': hi, 'count': cnt, 'desirable': desirable
        })
    desirable_zone = {'lo': 0.15, 'hi': 0.35}

    # --- 学习效率指数 ---
    efficiency_timeline = []
    for r in studylog:
        reviewed = r['wordsReviewed']
        eff = round((r['scoreEASY'] + r['scoreGOOD']) / reviewed, 3) if reviewed > 0 else None
        efficiency_timeline.append({
            'date': r['date'], 'efficiency': eff, 'reviewed': reviewed
        })

    # --- 间隔扩张：按难度档 ---
    # status 是学习阶段（New/Step1/Step2/Relearn/Reviewed=5），不是掌握程度：
    # 按 status 分组时，已学完的词库里永远只有一根 Status 5 的柱子。
    # 改按难度档分组，能看出"难度越低，间隔被扩张得越长"。
    expansion_buckets = [
        ('0.0-0.1', 0.0, 0.1), ('0.1-0.2', 0.1, 0.2), ('0.2-0.3', 0.2, 0.3),
        ('0.3-0.4', 0.3, 0.4), ('0.4-0.5', 0.4, 0.5), ('0.5+', 0.5, float('inf')),
    ]
    expansion_curve = []
    for label, lo, hi in expansion_buckets:
        vals = sorted(w['daysBetweenReviews'] for w in words if lo <= w['difficulty'] < hi)
        if not vals:
            continue
        expansion_curve.append({
            'label': label,
            'avgInterval': round(sum(vals) / len(vals), 1),
            'medianInterval': round(vals[len(vals) // 2], 1),
            'count': len(vals),
        })

    # --- 周节律热力图 ---
    weekday_names = ['周一', '周二', '周三', '周四', '周五', '周六', '周日']
    weekly_data = defaultdict(lambda: [0, 0])  # [correct, total]
    last_day = None
    for r in studylog:
        dt = datetime.strptime(r['date'], '%Y-%m-%d')
        if last_day is None or dt > last_day:
            last_day = dt
        week_key = dt.strftime('%Y-W%W')
        wd = dt.weekday()
        weekly_data[(week_key, wd)][0] += r['scoreEASY'] + r['scoreGOOD']
        weekly_data[(week_key, wd)][1] += r['wordsReviewed']

    rhythm_matrix = []
    if last_day is not None:
        # 以最后一条记录所在周的周一为锚，向前取连续 8 个自然周。
        # 旧写法 all_weeks[-8:] 取的是"最近 8 个有数据的周"，中间断档时横轴会偷偷跨更久。
        monday = last_day - timedelta(days=last_day.weekday())
        for k in range(7, -1, -1):
            week = (monday - timedelta(days=7 * k)).strftime('%Y-W%W')
            row = {'week': week}
            for wd in range(7):
                correct, total = weekly_data.get((week, wd), (0, 0))
                row[weekday_names[wd]] = round(correct / total * 100, 1) if total > 0 else None
            rhythm_matrix.append(row)

    # --- 顽固词 Top 15 ---
    # 旧实现按 status != 5 过滤，但本作 status=5 就是 Reviewed（已学完），
    # 已学词几乎全为 5，榜单恒空。改为"最近一次没通过（< Good）或仍在学习/重学阶段"。
    lapsed = [w for w in words if w['lastScore'] < 0.8 or w['status'] != 5]
    hard_words = sorted(lapsed, key=lambda x: (-x['difficulty'], x['daysBetweenReviews']))[:15]
    hard_list = [{
        'word': w['headWord'],
        'pos': w.get('pos') or '',
        'tranCN': w.get('tranCN') or '',
        'difficulty': round(w['difficulty'], 4),
        'interval': round(w['daysBetweenReviews'], 1),
        'score': round(w['lastScore'], 2),
        'status': w['status'],
        'book': w.get('book', ''),
    } for w in hard_words]

    return {
        'forgettingData': forgetting_data,
        'bjorkPoints': bjork_points,
        'diffBucketsDetailed': diff_buckets_detailed,
        'desirableZone': desirable_zone,
        'efficiencyTimeline': efficiency_timeline,
        'expansionCurve': expansion_curve,
        'rhythmMatrix': rhythm_matrix,
        'hardWords': hard_list,
        'weekdayNames': weekday_names,
        'totalWords': len(words),
        'totalStudyDays': len(studylog),
        'lapseCount': len(lapsed),
        'bookCount': len({w.get('book', '') for w in words}),
    }


def _empty_page2():
    return {
        'forgettingData': [], 'bjorkPoints': [], 'diffBucketsDetailed': [],
        'desirableZone': {'lo': 0.15, 'hi': 0.35},
        'efficiencyTimeline': [], 'expansionCurve': [],
        'rhythmMatrix': [], 'hardWords': [],
        'weekdayNames': ['周一', '周二', '周三', '周四', '周五', '周六', '周日'],
        'totalWords': 0, 'totalStudyDays': 0, 'lapseCount': 0, 'bookCount': 0,
    }


# HTML 模板（Vue 3 + Chart.js + Cyber Anime CSS）
# ============================================================

def generate_html(data_json, vue_path, chart_path):
    """从独立模板文件读取 HTML，内联 Vue + Chart.js，注入 JSON 数据。

    Template 中的 CDN <script src="...vue..."> 和 <script src="...chart.js...">
    会被替换为内联 <script> 块，产出真正的单文件 HTML。"""
    import os as _os
    # 脚本在 Tools/ 则模板在 Tools/，在 Resources/ 则模板在 Resources/
    tpl_path = _os.path.join(_SCRIPT_DIR, "dashboard.template.html")
    if not _os.path.exists(tpl_path):
        tpl_path = _os.path.join(_SCRIPT_DIR, "..", "Tools", "dashboard.template.html")
    with open(tpl_path, "r", encoding="utf-8") as _f:
        template = _f.read()

    # 读取 JS 库
    with open(vue_path, "r", encoding="utf-8") as _f:
        vue_js = _f.read()
    with open(chart_path, "r", encoding="utf-8") as _f:
        chart_js = _f.read()

    # 内联替换模板中的占位符
    inline_block = (
        '<script>\n' + vue_js + '\n</script>\n'
        '<script>\n' + chart_js + '\n</script>'
    )
    template = template.replace('<!-- VUE_INLINE -->', inline_block)

    return template.replace("___DATA_JSON___", data_json)


# ============================================================
# Main
# ============================================================
# (旧内联模板已移除——模板现在独立在 dashboard.template.html 中)
def main():
    print("[*] ToastFish Dashboard Generator")
    print(f"   数据库: {DB_PATH}")
    print(f"   输出: {OUTPUT_PATH}")

    if not os.path.exists(DB_PATH):
        print(f"[ERROR] 数据库未找到: {DB_PATH}")
        sys.exit(1)

    conn = get_db()
    try:
        studylog, global_config, counts, words, all_words, book_stats, current_book = read_all_data(conn)
        essay_logs = read_essay_logs(conn)  # 必须在 conn.Close() 之前读取
    finally:
        conn.close()

    print(f"   StudyLog: {len(studylog)} 条记录")
    print(f"   已学词: {len(words)} 条 (当前词库: {current_book})")
    print(f"   全部词库已学词: {len(all_words)} 条 (按 headWord 去重)")
    print(f"   词库数: {len(book_stats)}")
    print(f"   EssayLog: {len(essay_logs)} 条记录")

    # 计算指标
    page1 = compute_page1(studylog, all_words, counts, book_stats, current_book)
    page2 = compute_page2(studylog, all_words)
    # current-scope variant (2026-10-18): academic page can toggle scope
    page2['currentScope'] = compute_page2(studylog, words)

    # 组装数据
    dashboard_data = {
        'page1': page1,
        'page2': page2,
        'essayLogs': essay_logs,
        'meta': {
            'generatedAt': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
            'currentBook': current_book,
            'bookCount': len(book_stats),
            'totalStudyLogs': len(studylog),
            'totalWords': len(words),
            'totalWordsAll': len(all_words),
        }
    }

    # 序列化（ensure_ascii=False 保留中文）
    data_json = json.dumps(dashboard_data, ensure_ascii=False, separators=(',', ':'))

    # 生成 HTML（内联 Vue + Chart.js，零 CDN 依赖）
    vue_path = os.path.join(_RES_DIR, "vue.min.js")
    chart_path = os.path.join(_RES_DIR, "chart.min.js")
    if not os.path.exists(vue_path) or not os.path.exists(chart_path):
        print("[ERROR] JS 库文件缺失，请确保 vue.min.js 和 chart.min.js 在 Resources 目录")
        print(f"   Vue: {vue_path}")
        print(f"   Chart: {chart_path}")
        sys.exit(1)
    html = generate_html(data_json, vue_path, chart_path)

    # 写入
    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, 'w', encoding='utf-8') as f:
        f.write(html)

    size_kb = os.path.getsize(OUTPUT_PATH) / 1024
    print(f"[OK] 已生成: {OUTPUT_PATH} ({size_kb:.0f} KB)")

    # 自动启动 API 服务器（用于仪表盘删除功能）
    _ensure_api_server()


def _ensure_api_server():
    """检查 essay_api.py 是否在运行，未运行则后台启动。"""
    PORT = 8765
    # 检查端口是否已监听
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.connect(('127.0.0.1', PORT))
        s.close()
        # 端口在用，服务器已在运行
        return
    except (ConnectionRefusedError, OSError):
        s.close()

    api_script = os.path.join(_SCRIPT_DIR, "essay_api.py")
    if not os.path.exists(api_script):
        print("[!] essay_api.py 未找到，删除功能不可用")
        return

    try:
        if sys.platform == 'win32':
            subprocess.Popen(
                [sys.executable, api_script],
                creationflags=subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS,
                close_fds=True
            )
        else:
            subprocess.Popen(
                [sys.executable, api_script],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                start_new_session=True
            )
        print(f"[*] API 服务器已启动 (localhost:{PORT})")
    except Exception as e:
        print(f"[!] API 服务器启动失败: {e}")


if __name__ == '__main__':
    main()
