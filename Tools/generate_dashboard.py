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

    return studylog, global_config, counts, words, book_stats, current_book


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

def compute_page1(studylog, words, counts, book_stats, current_book):
    # --- KPI ---
    total_reviewed = sum(r['wordsReviewed'] for r in studylog)
    total_easy = sum(r['scoreEASY'] for r in studylog)
    total_good = sum(r['scoreGOOD'] for r in studylog)
    total_hard = sum(r['scoreHARD'] for r in studylog)
    total_again = sum(r['scoreAGAIN'] for r in studylog)
    total_scored = total_easy + total_good + total_hard + total_again
    accuracy = (total_easy + total_good) / total_scored * 100 if total_scored > 0 else 0
    max_daily = max(r['wordsReviewed'] for r in studylog) if studylog else 0

    # 连续天数
    dates = sorted(set(r['date'] for r in studylog))
    streak = 1
    max_streak = 1
    for i in range(1, len(dates)):
        d0 = datetime.strptime(dates[i-1], '%Y-%m-%d')
        d1 = datetime.strptime(dates[i], '%Y-%m-%d')
        if (d1 - d0).days == 1:
            streak += 1
            max_streak = max(max_streak, streak)
        else:
            streak = 1
    total_days = len(dates)

    # 当前词库进度
    book_total = counts.get(current_book, {}).get('number', 0)
    book_current = counts.get(current_book, {}).get('current', 0)
    book_mastered = sum(1 for w in words if w['status'] == 5)

    kpis = [
        {'icon': '📝', 'value': total_reviewed, 'label': '累计学习词次', 'format': 'int'},
        {'icon': '🔥', 'value': max_streak, 'label': '最长连续天数', 'format': 'int'},
        {'icon': '📅', 'value': total_days, 'label': '有效学习天数', 'format': 'int'},
        {'icon': '🎯', 'value': round(accuracy, 1), 'label': '总正确率 %', 'format': 'pct'},
        {'icon': '⚡', 'value': max_daily, 'label': '最高单日词数', 'format': 'int'},
        {'icon': '📚', 'value': round(book_current / book_total * 100, 1) if book_total > 0 else 0,
         'label': f'{current_book} 进度 %', 'format': 'pct'},
    ]

    # --- 每日趋势 ---
    daily_trend = []
    for r in studylog:
        daily_trend.append({
            'date': r['date'],
            'words': r['wordsReviewed'],
            'easy': r['scoreEASY'],
            'good': r['scoreGOOD'],
            'hard': r['scoreHARD'],
            'again': r['scoreAGAIN'],
            'accuracy': round((r['scoreEASY'] + r['scoreGOOD']) / r['wordsReviewed'] * 100, 1)
            if r['wordsReviewed'] > 0 else 0
        })

    # --- 累计 ---
    cumulative = []
    acc = 0
    for r in studylog:
        acc += r['wordsReviewed']
        cumulative.append({'date': r['date'], 'cumulative': acc})

    # --- 学习日历热力图（最近 20 周） ---
    today = datetime.now().date()
    # 找到最近的星期日
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
            'week': (d - start_date).days // 7
        })
        d += timedelta(days=1)

    # --- 难度分布 ---
    diff_buckets = [
        {'label': '0.0-0.1', 'lo': 0.0, 'hi': 0.1},
        {'label': '0.1-0.2', 'lo': 0.1, 'hi': 0.2},
        {'label': '0.2-0.3', 'lo': 0.2, 'hi': 0.3},
        {'label': '0.3-0.4', 'lo': 0.3, 'hi': 0.4},
        {'label': '0.4-0.5', 'lo': 0.4, 'hi': 0.5},
        {'label': '0.5-1.0', 'lo': 0.5, 'hi': 1.0},
    ]
    diff_hist = []
    for b in diff_buckets:
        cnt = sum(1 for w in words if b['lo'] <= w['difficulty'] < b['hi'])
        diff_hist.append({'label': b['label'], 'count': cnt})

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
        cnt = sum(1 for w in words if b['lo'] < w['daysBetweenReviews'] <= b['hi'])
        interval_hist.append({'label': b['label'], 'count': cnt})

    # --- 词库进度排行 ---
    book_progress = []
    for name, stats in sorted(book_stats.items(),
                              key=lambda x: x[1]['learned'], reverse=True):
        if stats['total'] > 0:
            book_progress.append({
                'name': name,
                'total': stats['total'],
                'learned': stats['learned'],
                'mastered': stats['mastered'],
                'pct': round(stats['learned'] / stats['total'] * 100, 1)
            })

    # --- Status 分布 ---
    status_dist = {}
    for w in words:
        s = w['status']
        status_dist[s] = status_dist.get(s, 0) + 1
    status_pie = [{'name': f'Status {k}', 'count': v} for k, v in status_dist.items()]

    return {
        'kpis': kpis,
        'dailyTrend': daily_trend,
        'cumulative': cumulative,
        'calendarData': calendar_data,
        'diffHist': diff_hist,
        'intervalHist': interval_hist,
        'bookProgress': book_progress,
        'statusPie': status_pie,
        'totalReviewed': total_reviewed,
        'totalEasy': total_easy,
        'totalGood': total_good,
        'totalHard': total_hard,
        'totalAgain': total_again,
    }


# ============================================================
# 指标计算 — 页面 2（学术分析）
# ============================================================

def compute_page2(studylog, words):
    if not words:
        return _empty_page2()

    # --- 遗忘曲线：按间隔桶计算正确率 ---
    forgetting_buckets = [
        ('<1天', 0, 1), ('1-3天', 1, 3), ('3-7天', 3, 7),
        ('1-2周', 7, 14), ('2-4周', 14, 30), ('1-2月', 30, 60), ('>2月', 60, 9999),
    ]
    forgetting_data = []
    for label, lo, hi in forgetting_buckets:
        bucket_words = [w for w in words if lo < w['daysBetweenReviews'] <= hi]
        n = len(bucket_words)
        correct = sum(1 for w in bucket_words if w['lastScore'] >= 0.7)
        rate = round(correct / n * 100, 1) if n > 0 else 0
        forgetting_data.append({
            'label': label, 'midpoint': (lo + min(hi, 120)) / 2,
            'count': n, 'rate': rate
        })

    # 指数拟合参数（需要在半衰期前计算）
    fit_params = _exp_fit(forgetting_data)

    # 记忆半衰期估算
    half_life = _estimate_half_life(forgetting_data, fit_params)

    # 为每个桶计算拟合值
    a_fit, b_fit = fit_params['a'], fit_params['b']
    for fd in forgetting_data:
        fd['fittedRate'] = round(a_fit * math.exp(-fd['midpoint'] / b_fit) * 100, 1) if b_fit > 0 else 0

    # --- Bjork 双维模型 ---
    bjork_points = []
    for w in words:
        score = w['lastScore']
        interval = w['daysBetweenReviews']
        # 存储强度代理 S = -t / ln(R)，R=score 代理
        if score > 0.1 and interval > 0:
            s = min(score, 0.99)  # log(1)=0 would divide by zero
            storage = -interval / math.log(max(s, 0.02))
        else:
            storage = 0.5
        bjork_points.append({
            'word': w['headWord'],
            'storage': round(min(storage, 200), 1),
            'retrieval': round(score, 2),
            'status': w['status'],
            'difficulty': round(w['difficulty'], 4)
        })

    # --- 难度分布 + 合意区 ---
    diff_buckets_detailed = []
    for i in range(10):
        lo = i * 0.1
        hi = (i + 1) * 0.1
        cnt = sum(1 for w in words if lo <= w['difficulty'] < hi)
        diff_buckets_detailed.append({'label': f'{lo:.1f}', 'lo': lo, 'hi': hi, 'count': cnt})
    desirable_zone = {'lo': 0.15, 'hi': 0.35}

    # --- 学习效率指数 ---
    efficiency_timeline = []
    for i, r in enumerate(studylog):
        if r['wordsReviewed'] > 0:
            eff = round((r['scoreEASY'] + r['scoreGOOD']) / r['wordsReviewed'], 3)
        else:
            eff = 0
        efficiency_timeline.append({'date': r['date'], 'efficiency': eff})

    # --- 间隔扩张曲线（按 status） ---
    status_intervals = {}
    for w in words:
        s = w['status']
        if s not in status_intervals:
            status_intervals[s] = []
        status_intervals[s].append(w['daysBetweenReviews'])
    expansion_curve = []
    for s in sorted(status_intervals.keys()):
        vals = status_intervals[s]
        expansion_curve.append({
            'status': s,
            'label': f'Status {s}',
            'avgInterval': round(sum(vals) / len(vals), 1),
            'medianInterval': round(sorted(vals)[len(vals)//2], 1),
            'count': len(vals)
        })

    # --- 周节律热力图 ---
    weekday_names = ['周一', '周二', '周三', '周四', '周五', '周六', '周日']
    # 按周分组
    weekly_data = defaultdict(lambda: [0, 0])  # [correct, total]
    for r in studylog:
        dt = datetime.strptime(r['date'], '%Y-%m-%d')
        week_key = dt.strftime('%Y-W%W')
        wd = dt.weekday()
        correct = r['scoreEASY'] + r['scoreGOOD']
        weekly_data[(week_key, wd)][0] += correct
        weekly_data[(week_key, wd)][1] += r['wordsReviewed']

    # 构建矩阵：取最近 8 周
    all_weeks = sorted(set(k[0] for k in weekly_data.keys()))
    recent_weeks = all_weeks[-8:] if len(all_weeks) > 8 else all_weeks

    rhythm_matrix = []
    for week in recent_weeks:
        row = {'week': week}
        for wd in range(7):
            correct, total = weekly_data.get((week, wd), (0, 0))
            rate = round(correct / total * 100, 1) if total > 0 else None
            row[weekday_names[wd]] = rate
        rhythm_matrix.append(row)

    # --- 顽固词 Top 15 ---
    hard_words = sorted(
        [w for w in words if w['status'] != 5],
        key=lambda x: x['difficulty'], reverse=True
    )[:15]
    hard_list = [{
        'word': w['headWord'],
        'pos': w.get('pos', ''),
        'tranCN': w.get('tranCN', ''),
        'difficulty': round(w['difficulty'], 4),
        'interval': round(w['daysBetweenReviews'], 1),
        'score': round(w['lastScore'], 2),
        'status': w['status']
    } for w in hard_words]

    return {
        'forgettingData': forgetting_data,
        'halfLife': round(half_life, 1),
        'fitParams': fit_params,
        'bjorkPoints': bjork_points,
        'diffBucketsDetailed': diff_buckets_detailed,
        'desirableZone': desirable_zone,
        'efficiencyTimeline': efficiency_timeline,
        'expansionCurve': expansion_curve,
        'rhythmMatrix': list(rhythm_matrix),
        'hardWords': hard_list,
        'weekdayNames': weekday_names,
        'totalWords': len(words),
        'totalStudyDays': len(studylog),
    }


def _empty_page2():
    return {
        'forgettingData': [], 'halfLife': 0, 'fitParams': {'a': 0, 'b': 0, 'r2': 0},
        'bjorkPoints': [], 'diffBucketsDetailed': [],
        'desirableZone': {'lo': 0.15, 'hi': 0.35},
        'efficiencyTimeline': [], 'expansionCurve': [],
        'rhythmMatrix': [], 'hardWords': [],
        'weekdayNames': ['周一', '周二', '周三', '周四', '周五', '周六', '周日'],
        'totalWords': 0, 'totalStudyDays': 0
    }


def _estimate_half_life(forgetting_data, fit_params):
    """从遗忘曲线数据中估算记忆半衰期（天）。

    优先使用指数拟合参数: t₁/₂ = b * ln(2)。
    如果拟合失败，回退到数据分桶插值。"""
    b = fit_params.get('b', 0)

    # 如果拟合可信（b > 0 且 r² > 0.5），直接按模型计算
    if b > 0 and fit_params.get('r2', 0) > 0.5:
        return b * math.log(2)

    # 回退：从原始桶数据插值（仅使用含足够数据的桶）
    points = [(d['midpoint'], d['rate']) for d in forgetting_data if d['count'] >= 10]
    if len(points) < 2:
        points = [(d['midpoint'], d['rate']) for d in forgetting_data if d['count'] > 0]

    if len(points) < 2:
        return 90.0

    if all(p[1] > 85 for p in points):
        return max(points[-1][0] * 3, 100.0)

    if all(p[1] > 50 for p in points):
        return points[-1][0] * 2

    if all(p[1] < 50 for p in points):
        return points[0][0] * 0.5

    for i in range(len(points) - 1):
        x1, y1 = points[i]
        x2, y2 = points[i + 1]
        if (y1 - 50) * (y2 - 50) <= 0:
            if abs(y1 - y2) < 0.01:
                return (x1 + x2) / 2
            t = (50 - y1) / (y2 - y1)
            return x1 + t * (x2 - x1)
    return 90.0


def _exp_fit(forgetting_data):
    """简单指数拟合 R = a * exp(-t/b)，返回参数"""
    points = [(d['midpoint'], d['rate'] / 100) for d in forgetting_data if d['count'] > 0]
    if len(points) < 3:
        return {'a': 1.0, 'b': 60.0, 'r2': 0.0}

    x_vals = [p[0] for p in points]
    y_vals = [p[1] for p in points]

    # 如果正确率始终 > 90%，几乎无遗忘——标记为极长半衰期
    if all(v > 0.90 for v in y_vals):
        return {'a': 1.0, 'b': 300.0, 'r2': 0.90}

    # 检查 y 值是否随 x 单调递减（遗忘曲线的必要条件）
    is_decreasing = all(y_vals[i] >= y_vals[i+1] - 0.02 for i in range(len(y_vals)-1))

    # 对数线性化: ln(R) = ln(a) - t/b → y' = ln(y), slope = -1/b, intercept = ln(a)
    try:
        log_y = [math.log(max(v, 0.01)) for v in y_vals]
        n = len(x_vals)

        # 纯 Python 线性回归: slope, intercept
        mean_x = sum(x_vals) / n
        mean_ly = sum(log_y) / n
        num = sum((x_vals[i] - mean_x) * (log_y[i] - mean_ly) for i in range(n))
        den = sum((x_vals[i] - mean_x) ** 2 for i in range(n))

        if den > 0:
            slope = num / den
            # b 必须为正（遗忘 → 指数衰减）
            if slope >= 0 or not is_decreasing:
                # 斜率非负 → 无遗忘趋势，用超长半衰期
                return {'a': 1.0, 'b': 300.0, 'r2': 0.85}
            b_val = -1.0 / slope
        else:
            return {'a': 1.0, 'b': 60.0, 'r2': 0.0}

        intercept = mean_ly - slope * mean_x
        a_val = math.exp(intercept)

        # R²
        mean_y = sum(y_vals) / n
        ss_res = sum((y_vals[i] - a_val * math.exp(-x_vals[i] / b_val)) ** 2 for i in range(n))
        ss_tot = sum((y_vals[i] - mean_y) ** 2 for i in range(n))
        r2 = 1 - ss_res / ss_tot if ss_tot > 0 else 0

        # 限制 b 在合理范围
        b_val = max(1.0, min(500.0, b_val))
        a_val = max(0.5, min(1.5, a_val))
        r2 = max(0.0, min(1.0, r2))

    except Exception:
        a_val, b_val, r2 = 1.0, 60.0, 0.0

    return {
        'a': round(float(a_val), 4),
        'b': round(float(b_val), 1),
        'r2': round(float(r2), 4)
    }


# ============================================================
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
        studylog, global_config, counts, words, book_stats, current_book = read_all_data(conn)
        essay_logs = read_essay_logs(conn)  # 必须在 conn.Close() 之前读取
    finally:
        conn.close()

    print(f"   StudyLog: {len(studylog)} 条记录")
    print(f"   已学词: {len(words)} 条 (当前词库: {current_book})")
    print(f"   词库数: {len(book_stats)}")
    print(f"   EssayLog: {len(essay_logs)} 条记录")

    # 计算指标
    page1 = compute_page1(studylog, words, counts, book_stats, current_book)
    page2 = compute_page2(studylog, words)

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
