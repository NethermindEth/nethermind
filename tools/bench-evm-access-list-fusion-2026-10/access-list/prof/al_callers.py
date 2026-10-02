import sys, re, collections, json, glob
sys.path.insert(0, r'C:/praca/neth-claude/rpcbench/opt')
import prof_areas as pa
run = sys.argv[1]; c = int(sys.argv[2]) if len(sys.argv) > 2 else 30
lv = json.load(open(glob.glob(f'{run}/gcprof-*-perf-c{c}-0-c{c}.json')[0]))['levels'][0]
rps = lv['requestsPerSecond']
syms, samples = pa.parse(f'{run}/script-c{c}.txt.gz')
t0, act = pa.active_window(syms, samples)
ka = (1000 / pa.HZ) / (rps * act)
print(f'rps {rps:.1f} active {act:.1f}s ka {ka:.5f} ms/sample samples {len(samples)}')
AL = re.compile(r'HashSet`1\[Nethermind\.Core\.StorageCell\]|JournalSet`1|StackAccessTracker')
ADDRSET = re.compile(r'JournalSet`1\[System\.__Canon\]')
CELLSET = re.compile(r'HashSet`1\[Nethermind\.Core\.StorageCell\]|JournalSet`1\[Nethermind\.Core\.StorageCell\]')
CALLER = re.compile(r'::(Op\w+|Instruction\w+|ExecuteEvmCall|ExecuteEvmTransaction|BuildExecutionEnvironment|WarmUp\w*|\w*Prewarm\w*|ExecuteCall|ExecuteTransaction|RunByteCode|CreateFullCallFrame|TryInlineStaticPrecompileCall|PopAndRestoreParentState|HandleRevert|HandleException|HandleFailure|InitializeFrameCore|PrepareFrame\w*)$')
tot = collections.Counter(); by = collections.defaultdict(collections.Counter); leafc = collections.defaultdict(collections.Counter)
chain = collections.Counter()
n_act = 0
for comm, fr, tm in samples:
    if tm - t0 > act: continue
    n_act += 1
    S = [syms[i] for i in fr]
    idx = next((j for j, s in enumerate(S) if AL.search(s.full)), None)
    if idx is None: continue
    al = S[idx]
    kind = 'addr' if ADDRSET.search(al.full) else 'cell' if CELLSET.search(al.full) else 'tracker'
    # outermost access-list frame (method entry)
    top_al = max(j for j, s in enumerate(S) if AL.search(s.full))
    caller = next((S[j].disp for j in range(top_al + 1, len(S)) if CALLER.search(S[j].full)), '?')
    # what's inside: leaf frame
    leaf = S[0].disp
    alm = S[idx].disp
    tot[kind] += 1
    by[kind][(alm if idx == top_al else S[top_al].disp + ' > ' + alm, caller)] += 1
    leafc[kind][leaf] += 1
    chain[' < '.join(s.disp for s in S[:top_al + 4])] += 1
print('active samples', n_act)
for k in tot:
    print(f'\n== {k}: {tot[k]} samples, {tot[k]*ka:.3f} ms/req')
    for (m, cl), v in by[k].most_common(30): print(f'  {v*ka:.3f}  {v:5d}  {m:70s} <- {cl}')
    print('  leaves:')
    for l, v in leafc[k].most_common(15): print(f'    {v*ka:.3f}  {v:5d}  {l}')
print('\n== top chains')
for ch, v in chain.most_common(40): print(f'{v*ka:.3f} {v:5d} {ch}')
