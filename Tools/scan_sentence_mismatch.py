# -*- coding: utf-8 -*-
"""扫描 CET6_3 例句与 headWord 不匹配的条目（分层匹配，与 C# 运行时算法同构）"""
import sqlite3, re, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

DB = r'E:\ToastFish.v3.0\ToastFish\Resources\inami.db'

_IRR_RAW = """
arise arose arisen | awake awoke awoken | bear bore borne | beat beat beaten
become became become | begin began begun | bend bent bent | bind bound bound
bite bit bitten | bleed bled bled | blow blew blown | break broke broken
breed bred bred | bring brought brought | build built built | burn burnt burned
buy bought bought | catch caught caught | choose chose chosen | cling clung clung
come came come | creep crept crept | deal dealt dealt | dig dug dug
dive dove dived | do did done | draw drew drawn | dream dreamt dreamed
drink drank drunk | drive drove driven | dwell dwelt dwelt | eat ate eaten
fall fell fallen | feed fed fed | feel felt felt | fight fought fought
find found found | flee fled fled | fling flung flung | fly flew flown
forbid forbade forbidden | forget forgot forgotten | forgive forgave forgiven
freeze froze frozen | get got gotten | give gave given | go went gone
grind ground ground | grow grew grown | hang hung hung | hear heard heard
hide hid hidden | hold held held | keep kept kept | kneel knelt knelt
know knew known | lay laid laid | lead led led | leap leapt leaped
leave left left | lend lent lent | lie lay lain | light lit lit
lose lost lost | make made made | mean meant meant | meet met met
mistake mistook mistaken | mow mowed mown | overcome overcame overcome
pay paid paid | prove proved proven | ride rode ridden | ring rang rung
rise rose risen | run ran run | saw sawed sawn | say said said | see saw seen
seek sought sought | sell sold sold | send sent sent | shake shook shaken
shear sheared shorn | shine shone shone | shoot shot shot | show showed shown
shrink shrank shrunk | sing sang sung | sink sank sunk | sit sat sat
slay slew slain | sleep slept slept | slide slid slid | sling slung slung
smell smelt smelled | sneak snuck sneaked | sow sowed sown | speak spoke spoken
speed sped sped | spell spelt spelled | spend spent spent | spill spilt spilled
spin spun spun | spit spat spat | spoil spoilt spoiled | spring sprang sprung
stand stood stood | steal stole stolen | stick stuck stuck | sting stung stung
stink stank stunk | stride strode stridden | strike struck struck
strive strove striven | swear swore sworn | sweep swept swept
swell swelled swollen | swim swam swum | swing swung swung
take took taken | teach taught taught | tear tore torn | tell told told
think thought thought | throw threw thrown | thrust thrust thrust
tread trod trodden | understand understood understood | undergo underwent undergone
undertake undertook undertaken | wake woke woken | wear wore worn
weave wove woven | weep wept wept | win won won | wind wound wound
withdraw withdrew withdrawn | withstand withstood withstood
wring wrung wrung | write wrote written
"""
IRREGULAR = {}
for grp in _IRR_RAW.replace('\n', ' | ').split('|'):
    parts = grp.split()
    if len(parts) >= 2:
        base = parts[0]
        for form in parts[1:]:
            if form != base:
                IRREGULAR[form] = base

SUFFIXES = ["ies", "ied", "ier", "iest", "es", "ed", "ing", "ly", "er", "est", "ic", "s", "d", "n"]

def stem_match(hw, w):
    """第1/2层：精确 + 规则屈折还原"""
    if w == hw:
        return True
    for suf in SUFFIXES:
        if not w.endswith(suf) or len(w) - len(suf) < 2:
            continue
        stem = w[:-len(suf)]
        if stem == hw:                      # implemented -> implement
            return True
        if stem + 'e' == hw:                # allocating -> allocate
            return True
        if suf.startswith('i') and stem + 'y' == hw:   # studies -> study
            return True
        if len(stem) > 2 and stem[-1] == stem[-2] and stem[:-1] == hw:
            return True                     # stopped -> stop
    # -ing 双写 / 去e 的 ing 形式已覆盖；us->i 拉丁复数等罕见不管
    return False

def variant_norm(s):
    """英美拼写变体归一化：aesthetic/esthetic, humour/humor, catalogue/catalog"""
    return s.replace('ae', 'e').replace('oe', 'e').replace('our', 'or').replace('ogue', 'og')

def irregular_match(hw, w):
    """第4层：不规则动词（含前缀形式 withheld->withhold, misled->mislead）"""
    if IRREGULAR.get(w) == hw:
        return True
    for form, base in IRREGULAR.items():
        if w.endswith(form) and hw.endswith(base) and w[:-len(form)] == hw[:-len(base)]:
            return True
    return False

def base_match(hw, w):
    if stem_match(hw, w) or irregular_match(hw, w):
        return True
    if hw != variant_norm(hw) or w != variant_norm(w):
        if stem_match(variant_norm(hw), variant_norm(w)):
            return True
    # 名词不规则复数：man->men（含复合词）, wolf->wolves, criterion<->criteria
    if 'man' in hw and w == hw.replace('man', 'men'):
        return True
    if w.endswith('ves') and (w[:-3] + 'f' == hw or w[:-3] + 'fe' == hw):
        return True
    if hw.endswith('ion') and w == hw[:-2] + 'a':      # criterion -> criteria
        return True
    if hw.endswith('a') and w == hw[:-1] + 'on':       # criteria -> criterion
        return True
    return False

def word_match(hw, w):
    """分层匹配总入口"""
    hw = hw.lower(); w = w.lower().strip("'")
    if w.endswith("'s"):                    # pope's -> pope
        w = w[:-2]
    if base_match(hw, w):
        return True
    if '-' in w:                            # 第3层：连字符复合词
        if base_match(hw, w.replace('-', '')):      # co-operate -> cooperate
            return True
        for part in w.split('-'):
            if part and base_match(hw, part):       # multi-faceted -> facet
                return True
    return False

def sentence_match(hw, sent):
    hw = hw.strip().lower()
    tokens = re.findall(r"[^\W\d_][^\W\d_]*(?:['-][^\W\d_]+)*", sent, re.UNICODE)
    if ' ' in hw:  # 词组型 headWord：全部词都出现即算
        return all(any(word_match(p, t) for t in tokens) for p in hw.split())
    return any(word_match(hw, t) for t in tokens)

conn = sqlite3.connect(DB)
rows = conn.execute("SELECT wordRank, headWord, sentence, sentenceCN FROM CET6_3 ORDER BY wordRank").fetchall()
bad = [r for r in rows if r[2] and r[2].strip() and not sentence_match(r[1], r[2])]
print('TOTAL=%d  MISMATCH=%d' % (len(rows), len(bad)))
for r in bad:
    print('%d\t%s\t%s\t%s' % (r[0], r[1], (r[2] or '').replace('\t',' '), (r[3] or '').replace('\t',' ')))
conn.close()
