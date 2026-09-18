# Stage C scope and observation boundary

Input begins after a full precompile frame exists. The frame is fresh, non-create, standard-mainnet Amsterdam, with exact code/recipient address facts, call data, price inputs and a supplied leaf oracle. The output is a top-level substate projection or parent continuation token; output-copy execution and the next opcode are outside this interval.

| Branch | Raw classification | Settlement |
|---|---|---|
| Base/data overflow | `handleFailure`, out of gas; no Run or installed local gas | Snapshot/RIPEMD restore, failure tracing, top failure or parent halt restoration |
| Pricing out of gas | Same hard route; failed local gas remains uninstalled | Same |
| Returned `Result=false` | `PrecompileFailure` CallResult is mapped to `PrecompileOutOfGas` / `handleFailure` | Same hard route |
| Managed exception, top level | `PrecompileExecutionFailure` / `handleFailure` | Top failure substate |
| Managed exception, nested | `nestedPrecompileSoftFailure`; raw gas not cleared | Clear execution gas once, return execution gas, remove advanced refund, restore child state gas, restore world/RIPEMD, revert token |
| Success, top level | Ordinary success | `TraceTransactionActionEnd`, then `PrepareTopLevelSubstate` |
| Success, nested | Ordinary success | Incorporate advanced refund, refund child gas, `HandleRegularReturn`, `CommitToParent`, repay spill, parent continuation |
| Outer EvmException / Overflow | `handleFailure`; supplied exact throw-boundary machine while the admitted precompile remains current | Same hard route, Overflow maps to Other |
| Missing native dependency | No ordinary result | Process termination excluded |

Action trace is conditional; transfer log behavior, memory/action reports inside `HandleRegularReturn`, and world changes are observations of explicit adapters. The IR separates top-level and nested settlement effect lists. Hard failure does not call `HandleException`. The nested managed result carries the soft-failure control tag until settlement, so gas clearing has one owner.

There is no claim about the cryptographic/native leaf implementation, CLR execution, allocation/unsafe memory, concrete WorldJournal implementation/composition, cancellation, direct STATICCALL, TrySave/output-copy OOG, stack-result push, bytecode dispatch, outer throws after parent restoration or frame pop, complete frame-driver execution, or transaction/block reachability. The source profile identifies current working production bytes, including the already-present tracing correction; it is not a stock-upstream hash claim.

`FrontAgrees` relates each front adapter field independently, and `FrontPreservesParents` requires those callbacks to leave the parent stack unchanged. `Entry.Admitted` states the pre-execution boundary; `generated_execute_is_raw_admitted` derives the settlement-domain facts from that boundary and generated execution. Settlement uses the exact supplied primitive record on both sides, with the immediate child's non-create baseline and successful top-substate discriminants as explicit premises. Ancestor frames may be CREATE frames. These are adapter contracts; merely pinning a theorem file does not discharge them. Pricing uses the admitted extracted kernel and its separately checked wrapper refinement under UInt64 bounds.
