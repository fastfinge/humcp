# Throwaway: BM25 baseline for tool search over the schema corpus, and the re-rank prompts.
import json, glob, os, re, math, sys
from collections import Counter

def tok(s):
    s = re.sub(r'([a-z])([A-Z])', r'\1 \2', s)
    return [w for w in re.split(r'[^a-z0-9]+', s.lower()) if w and w not in STOP]
STOP = set("a an the to of in for and or on is it my me i this that with what by from be are as at you your can do how".split())

tools = []
for f in sorted(glob.glob('../../tests/fixtures/schemas/*/*.json')):
    d = json.load(open(f, encoding='utf-8'))
    server = os.path.basename(os.path.dirname(f))
    name = d.get('name') or os.path.basename(f)[:-5]
    desc = re.sub(r'\s+', ' ', (d.get('description') or '')).strip()
    first = re.split(r'(?<=[.!?])\s', desc)[0][:200]
    tools.append({"id": f"{server}/{name}", "server": server, "name": name, "first": first,
                  "doc": tok(server + ' ' + name + ' ' + name + ' ' + desc[:600])})

N = len(tools); avgdl = sum(len(t['doc']) for t in tools) / N
df = Counter(w for t in tools for w in set(t['doc']))
def bm25(q, k1=1.2, b=0.75):
    qs = tok(q); out = []
    for t in tools:
        tf = Counter(t['doc']); s = 0
        for w in qs:
            if w in tf:
                idf = math.log(1 + (N - df[w] + 0.5) / (df[w] + 0.5))
                s += idf * tf[w] * (k1 + 1) / (tf[w] + k1 * (1 - b + b * len(t['doc']) / avgdl))
        out.append((s, t['id']))
    return [i for s, i in sorted(out, reverse=True)]

queries = json.load(open('material/toolsearch/queries.json'))
K = int(sys.argv[1]) if len(sys.argv) > 1 else 15
hit3 = recallK = 0; jobs = []
byid = {t['id']: t for t in tools}
os.makedirs('material/prompts/toolsearch', exist_ok=True)
for n, q in enumerate(queries):
    ranked = bm25(q['q'])
    h = any(g in ranked[:3] for g in q['gold']); r = any(g in ranked[:K] for g in q['gold'])
    hit3 += h; recallK += r
    print(f"{'Y' if h else '.'}{'Y' if r else '.'} {q['q'][:60]:60} -> {ranked[:3]}")
    lines = [f"{i+1}. {byid[t]['server']} / {byid[t]['name']}: {byid[t]['first']}" for i, t in enumerate(ranked[:K])]
    prompt = ("Someone using an app that lists tools wants to do this:\n\n\"" + q['q'] + "\"\n\nHere are the candidate tools, numbered:\n\n"
              + "\n".join(lines) + "\n\nWhich three tools best match what they want to do, best first? "
              "Answer with the three numbers only, separated by commas, and nothing else.")
    p = f'material/prompts/toolsearch/q{n:02}.txt'
    open(p, 'w', encoding='utf-8').write(prompt)
    jobs.append({"label": f"tools-q{n:02}", "prompt": p, "maxNew": 12, "candidates": ranked[:K], "gold": q['gold']})
json.dump(jobs, open('material/jobs-toolsearch.json', 'w'), indent=1)
print(f"BM25: right tool in top 3 for {hit3}/{len(queries)}; in top {K} for {recallK}/{len(queries)}")
