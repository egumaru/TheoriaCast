"""Build the singing sheet and conservatively audit a complete ASR transcript."""
import difflib
import re
import unicodedata

BRACKETS = re.compile(r"\[[^\]]*\]|【[^】]*】|［[^］]*］|\([^)]*\)|（[^）]*）|<[^>]*>")
DIRECTION = re.compile(r"^(?:#|※|ト書き|演奏指示|間奏(?:$|[：:])|前奏(?:$|[：:])|後奏(?:$|[：:])|(?:短い|長い)?(?:ピアノ|ギター|ドラム|ストリングス)の(?:導入|間奏|ソロ)|(?:intro|outro|instrumental|verse|chorus|bridge)(?:\s*\d+)?$)",re.I)
DIGITS="〇一二三四五六七八九"
KANA=["","いち","に","さん","よん","ご","ろく","なな","はち","きゅう"]


def year_reading(number):
    parts=[]
    for unit,label in ((1000,"せん"),(100,"ひゃく"),(10,"じゅう"),(1,"")):
        d,number=divmod(number,unit)
        if not d:
            continue
        special={(3,1000):"さんぜん",(8,1000):"はっせん",(3,100):"さんびゃく",
                 (6,100):"ろっぴゃく",(8,100):"はっぴゃく"}
        parts.append(special.get((d,unit),("" if d==1 and unit>1 else KANA[d])+label))
    return "".join(parts)


def prepare(spec, changes):
    cleaned=[]
    for raw in spec["lyrics"].splitlines():
        line=BRACKETS.sub("",raw).strip()
        if line and not DIRECTION.search(line):
            if any(ch in line for ch in "[]【】［］()（）<>"):
                raise ValueError("閉じていない演奏指示があります。歌詞を確認してください。")
            cleaned.append(line)
    source="\n".join(cleaned)
    if not source:
        raise ValueError("歌うことばがありません。")
    rules={"根菜":"こんさい","気に入る":"きにいる","入る":"はいる","開ける":"あける","ページ":"ぺーじ"}
    if re.search(r"金融|株価|投資|取引|相場|為替",spec["theme"]+" "+source):
        rules["市場"]="しじょう"
    for y in re.findall(r"(?<![〇零一二三四五六七八九])[一二][〇零一二三四五六七八九]{3}(?=年)|(?<!\d)[12]\d{3}(?=年)",source):
        n=int("".join(str(DIGITS.index(c)) for c in y.replace("零","〇"))) if not y.isdigit() else int(y)
        rules[y+"年"]=year_reading(n)+"ねん"
    rules.update(spec.get("readings") or {})
    rules.update(changes)
    candidates={k:v for k,v in rules.items() if k in source}
    applied={}
    if not candidates:
        return source,applied,cleaned
    pattern=re.compile("|".join(map(re.escape,sorted(candidates,key=len,reverse=True))))
    def substitute(match):
        word=match[0]
        applied[word]=candidates[word]
        return candidates[word]
    return pattern.sub(substitute,source),applied,cleaned


def normalize(s):
    s=unicodedata.normalize("NFKC",s)
    return "".join(chr(ord(c)-96) if "ァ"<=c<="ヶ" else c.lower() for c in s if c.isalnum() or c=="ー")


def audit(sheet,raw_asr,converter):
    """A missing, inserted or reordered word must remain visible in the result."""
    def kana(s):
        if not converter:
            return normalize(s)
        # Convert each line separately. A newline inside a kana run can cause
        # pykakasi to repeat the previous line in the following token.
        return normalize("".join(token["hira"] for line in s.splitlines() for token in converter.convert(line)))
    expected=kana(sheet)
    heard=kana(raw_asr)
    issues=[]
    for tag,a,b,c,d in difflib.SequenceMatcher(None,expected,heard,autojunk=False).get_opcodes():
        if tag!="equal":
            issues.append("予定「"+(expected[a:b] or "なし")+"」／聞き取り「"+(heard[c:d] or "なし")+"」")
    if not heard:
        issues.append("声を聞き取れませんでした。")
    if re.search("[一-龯]",raw_asr) and any(k in raw_asr for k in ("市場","開く","開ける","入る")):
        issues.append("文字起こしに複数の読みがある漢字が残り、発音を確定できません。")
    return issues


def correction(original,sheet,asr,converter,applied):
    if not converter:
        return {}
    heard=normalize("".join(t["hira"] for t in converter.convert(asr)))
    revised={}
    for line,performed in zip(original,sheet.splitlines()):
        expected=normalize("".join(t["hira"] for t in converter.convert(performed)))
        if expected and expected not in heard:
            # Convert the already substituted line, retaining the user's chosen readings.
            kana=" ".join(t["hira"] for t in converter.convert(performed) if t["hira"].strip())
            if kana!=performed and applied.get(line)!=kana:
                revised[line]=kana
    return revised


def lyric_cues(original,sheet,segments,converter):
    """Map original lyric lines onto timed ASR segments, retaining the transcript separately."""
    def syllables(text):
        return normalize("".join(t["hira"] for t in converter.convert(text)))
    lines=[syllables(line) for line in sheet.splitlines()]
    heard=""; times=[]
    for segment in segments:
        chars=syllables(segment["text"])
        for i in range(len(chars)):
            times.append(segment["start"]+(segment["end"]-segment["start"])*i/len(chars))
        heard+=chars
    if not times:
        return []
    times.append(segments[-1]["end"])
    pairs={}
    for a,b,n in difflib.SequenceMatcher(None,"".join(lines),heard,autojunk=False).get_matching_blocks():
        pairs.update((a+i,b+i) for i in range(n))
    rows=[]; offset=0
    for line,chars in zip(original,lines):
        matched=[pairs[i] for i in range(offset,offset+len(chars)) if i in pairs]
        if matched:
            rows.append({"start":times[matched[0]],"end":times[matched[-1]+1],"text":line})
        offset+=len(chars)
    return rows
