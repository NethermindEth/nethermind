# Measurement and verification tools: EIP-2929 access lists and compare+PUSH2+JUMPI fusion (Oct 2026)

Not for merging. This side branch keeps the tools behind the results of two pieces of EVM work, so that the numbers can be traced to code. The changes themselves are on their own side branches:

| work | branch | head |
|---|---|---|
| EIP-2929 access lists, full version | `bench/evm-access-list-2929` | `01b51de744` |
| EIP-2929 access lists, JournalSet only | `bench/evm-access-list-journalset` | `4d5a26c39a` |
| compare+PUSH2+JUMPI fusion, after the repair | `bench/evm-compare-branch-fusion-repair` | `c68c81621e` |
| compare+PUSH2+JUMPI fusion, first version | `bench/evm-compare-branch-fusion` | `e4d1890168` |

Base for both is master `06bd7443f0`. The scripts were written for the build machines of that week: paths under `/root`, a bench lock at `/root/bench.lock`, one pinned core. Adjust them before reuse.

## access-list/

| path | what it is | result it backs |
|---|---|---|
| `prof/al_callers.py`, `prof/leaf_ips.txt`, `prof/disasm_*.txt`, `prof/prod_off.txt` | attribution of the EIP-2929 costs in a master `eth_call` profile, and the Tier1 JIT listing of `HashSet<StorageCell>.AddIfNotPresent` mapped to the sampled instructions | 0.76 ms/req at c = 30; 43% of samples waiting on the bucket load |
| `probe/` (`JournalSetOld.cs`, `Program.cs`) | set microbenchmark: the old JournalSet as baseline against the new one | cold add, warm hit, add+restore and clear timings |
| `chainprobe/` | runs `OpcodeChainBenchmarks.ExecuteContract` chains in a loop, median of N × 1 s, ns per executed opcode | chain gates (ABAB, base / full / JournalSet only) |
| `scripts/abab.sh`, `abc.sh`, `pairab.sh`, `g1_*.sh`, `setup_variants.sh` | interleaved chain runs, started only on a quiet machine and discarded when the load rose while they ran | the same |
| `scripts/mutants.sh` | the engineer's mutants (5) | 5 of 5 caught |
| `v_fuzz/AlDiffFuzz.cs`, `v_fuzz2.sh`, `v_fuzzrun.sh` | differential EVM fuzz, base vs head, 6 forks × 2 × 4000 transactions; three runs per transaction (access tracing, action tracing, untraced commit) on a reused pooled state | 48,000 transactions byte-identical |
| `v_probe/` | JournalSet probe against a model, with a structural check after every restore | 0 failures |
| `v_mut/` | the verifier's mutants (10) | 9 caught, 1 equivalent |
| `scripts/pyspec.sh`, `suites.sh`, `build_tests.sh` | suite and Pyspec runners | test results |

## fusion/repair/

| path | what it is | result it backs |
|---|---|---|
| `fix1.patch` … `fix12.patch`, `hh_fix*.cs` | the experiments on `VirtualMachine.HostHandlers.std.cs` that led to `a09dfa8f45` and `c68c81621e` (rejected ones included: keeping the code pointer, a 256-byte "starts a branch" table) | causes A and B |
| `disasm.sh`, `buildv.sh` | JIT listings of the handlers per variant | instruction counts (base 25, old fusion 34, final 30 on the unfused ISZERO path) |
| `maps.py`, `refreshpatch.py` | perf-map reading and the opcode table dump / per-case refresh diagnostics | cause C: dispatch through precode stubs, per-process code layout |
| `exp.sh`, `ana.py`, `ana2.py` | subset measurements, each run a fresh process, medians of clean iterations | subset table, head vs base |
| `xgate.py`, `gate2/` | the gate rule applied to base against itself, and the rerun verdict | 32 "regressions" of base against base |
| `mutate.py`, `tests.sh` | mutations of the final structure (13) and the test runner | 12 of 13 caught |
| `pyspec.sh` | Pyspec for head and base | identical failure sets |

Lost with the build machine and not here: the original gate driver (`gate1.sh`, `compare.py`, `gate.py`), and an uncommitted access-list variant whose remembered-address check used `ReferenceEquals`.
