# Common EIP Implementation Pitfalls

General patterns that apply across all EIP categories. Gas-dimension-specific patterns (multi-dimensional accounting, reservoir spill, nested frame gas pool separation) belong in `implementation-patterns.md` under the relevant category, not here.

| Pattern | Applies When | What to check |
|---|---|---|
| **Flag-off path divergence** | Any consensus EIP | New behavior leaks when `IsEipXXXXEnabled = false` — test flag-off explicitly with `OverridableReleaseSpec` |
| **Decorator/override chain break** | Any EIP adding an `IReleaseSpec` property | `ReleaseSpecDecorator` and `OverridableReleaseSpec` must mirror the new property; missing = flag silently defaults to `false` in decorated contexts |
| **Chain spec pipeline gap** | Any EIP with a flag | All 9+ files in the pipeline must be updated (see `implementation-patterns.md` Layer 1-5); missing = flag can't activate via chain spec on non-mainnet chains |
| **Timestamp vs block number activation** | Post-Cancun EIPs | Use `TransitionTimestamp`, not `TransitionBlockNumber` in `ChainSpecBasedSpecProvider`; wrong = EIP never activates on timestamp-based chains |
| **Spec MUST vs SHOULD confusion** | Any EIP review | MUST = mandatory (finding if violated), SHOULD = advisory (suggestion at most); don't report SHOULD non-compliance as bugs |
