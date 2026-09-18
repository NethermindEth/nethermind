# Frozen EIP-8037/8038 specification

Frozen on 2026-09-10. Each EIP is pinned to the latest commit that changed its source file at freeze time.

```text
EIP-8037 commit: 052029f3625328d6f51dec8e62a7090201e66f17
EIP-8038 commit: 8331fb3eed0a5366b28b25a016f1ad04fac0fa8e
EIP-152 commit: 5510973b40973b6aa774f04c9caba823c8ff8460
EIP-196 commit: 9e393a79d9937f579acbdcb234a67869259d5a96
EIP-197 commit: 9e393a79d9937f579acbdcb234a67869259d5a96
EIP-198 commit: 9e393a79d9937f579acbdcb234a67869259d5a96
EIP-2565 commit: 9e393a79d9937f579acbdcb234a67869259d5a96
EIP-1108 commit: 8cc38b9d7566132c5a05cef8d3b573d7fc2c44e8
EIP-1153 commit: 0904d24b579a008831f7a0e3ef2f1381dd8d28d1
EIP-2537 commit: 1dd2558f9a68d9453aed71c803fdda09d83c6e37
EIP-4844 commit: 70471d02d48a81ca963407abe9c48706059dc8e8
EIP-7951 commit: b55cdb0ee78a696327cf76d1c5cf8088d73499ca
EIP-2780 commit: 7243c92ba812437c64bae9fc6524ee269b29daa9
EIP-7610 commit: 7707fe333322ed68d1b5efa50bdb4f35909255a7
EIP-7928 commit: d2a64c2d4cc44f2f507577d0ebfb110dcc21d358
EIP-7825 commit: b55cdb0ee78a696327cf76d1c5cf8088d73499ca
EIP-7823 commit: b55cdb0ee78a696327cf76d1c5cf8088d73499ca
EIP-7883 commit: b55cdb0ee78a696327cf76d1c5cf8088d73499ca
EIP-7702 commit: bbc3f95844c37612a2f1b9e7477990bb717ecfa0
EIP-7708 commit: f7230c46a743313957d8f38a159bda934cc735b2
EIP-7778 commit: 295064f75fb2084196dd2e247a4abf6074defe9c; enabled: true
EIP-7939 commit: b55cdb0ee78a696327cf76d1c5cf8088d73499ca
EIP-7910 commit: 0c82d532192eca83ab5ce12b2a0d3e019c803066
EIP-8024 commit: 34b49095ca5f7343045da279f04e7ecd1e451393
EIP-6780 commit: 688e939c4e0968f0cbc6a4e79426b498869eec19
EIP-8246 commit: 9207c6011f526bd40abd79649484a1a342585bd4
Nethermind commit: b2478235e71e6a7ec2a509aa0155e25d5fdfff80
Fork activation timestamp/block: synthetic Amsterdam at timestamp 0 / block 0; mainnet activation is not set
```

The production mainnet schedule at the pinned Nethermind commit uses `18446744073709551614` (`ulong.MaxValue - 1`) as an Amsterdam placeholder, so it is deliberately not the proof configuration. The model selects the `Amsterdam` release rules directly and treats them as active from the synthetic genesis boundary.

The Nethermind revision above is the ancestor baseline, not an assertion that the current task worktree is byte-identical to that commit. A production refinement leaf identifies its current code by the source hashes in its generated extractor manifest. The worktree correction for EIP-8038 warm-`SELFDESTRUCT` beneficiary pricing is presently test evidence only: no account-pricing extraction profile pins that changed source yet, so agreement with either the baseline implementation or the corrected worktree is not a formal claim. The complete identity gate requires every production semantic source to be covered by one reproducible manifest (or committed at a newly pinned revision) before the complete theorem can be stated.

The executable-specification cross-check is pinned to [`ethereum/execution-specs@0cc100eb190b64b23baba72dac0165652eaec252`](https://github.com/ethereum/execution-specs/commit/0cc100eb190b64b23baba72dac0165652eaec252), the head of its `forks/amsterdam` branch at freeze time.

## Primary sources

- [EIP-8037 pinned source](https://github.com/ethereum/EIPs/blob/052029f3625328d6f51dec8e62a7090201e66f17/EIPS/eip-8037.md)
- [EIP-8038 pinned source](https://github.com/ethereum/EIPs/blob/8331fb3eed0a5366b28b25a016f1ad04fac0fa8e/EIPS/eip-8038.md)
- [EIP-152 pinned source](https://github.com/ethereum/EIPs/blob/5510973b40973b6aa774f04c9caba823c8ff8460/EIPS/eip-152.md)
- [EIP-196 pinned source](https://github.com/ethereum/EIPs/blob/9e393a79d9937f579acbdcb234a67869259d5a96/EIPS/eip-196.md)
- [EIP-197 pinned source](https://github.com/ethereum/EIPs/blob/9e393a79d9937f579acbdcb234a67869259d5a96/EIPS/eip-197.md)
- [EIP-198 pinned source](https://github.com/ethereum/EIPs/blob/9e393a79d9937f579acbdcb234a67869259d5a96/EIPS/eip-198.md)
- [EIP-2565 pinned source](https://github.com/ethereum/EIPs/blob/9e393a79d9937f579acbdcb234a67869259d5a96/EIPS/eip-2565.md)
- [EIP-1108 pinned source](https://github.com/ethereum/EIPs/blob/8cc38b9d7566132c5a05cef8d3b573d7fc2c44e8/EIPS/eip-1108.md)
- [EIP-1153 pinned source](https://github.com/ethereum/EIPs/blob/0904d24b579a008831f7a0e3ef2f1381dd8d28d1/EIPS/eip-1153.md)
- [EIP-2537 pinned source](https://github.com/ethereum/EIPs/blob/1dd2558f9a68d9453aed71c803fdda09d83c6e37/EIPS/eip-2537.md)
- [EIP-4844 pinned source](https://github.com/ethereum/EIPs/blob/70471d02d48a81ca963407abe9c48706059dc8e8/EIPS/eip-4844.md)
- [EIP-7951 pinned source](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/EIPS/eip-7951.md)
- [EIP-2780 pinned source](https://github.com/ethereum/EIPs/blob/7243c92ba812437c64bae9fc6524ee269b29daa9/EIPS/eip-2780.md)
- [EIP-7610 pinned source](https://github.com/ethereum/EIPs/blob/7707fe333322ed68d1b5efa50bdb4f35909255a7/EIPS/eip-7610.md)
- [EIP-7928 pinned source](https://github.com/ethereum/EIPs/blob/d2a64c2d4cc44f2f507577d0ebfb110dcc21d358/EIPS/eip-7928.md)
- [EIP-7825 pinned source](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/EIPS/eip-7825.md)
- [EIP-7823 pinned source](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/EIPS/eip-7823.md)
- [EIP-7883 pinned source](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/EIPS/eip-7883.md)
- [EIP-7702 pinned source](https://github.com/ethereum/EIPs/blob/bbc3f95844c37612a2f1b9e7477990bb717ecfa0/EIPS/eip-7702.md)
- [EIP-7708 pinned source](https://github.com/ethereum/EIPs/blob/f7230c46a743313957d8f38a159bda934cc735b2/EIPS/eip-7708.md)
- [EIP-7778 pinned source](https://github.com/ethereum/EIPs/blob/295064f75fb2084196dd2e247a4abf6074defe9c/EIPS/eip-7778.md)
- [EIP-7939 pinned source](https://github.com/ethereum/EIPs/blob/b55cdb0ee78a696327cf76d1c5cf8088d73499ca/EIPS/eip-7939.md)
- [EIP-7910 pinned source](https://github.com/ethereum/EIPs/blob/0c82d532192eca83ab5ce12b2a0d3e019c803066/EIPS/eip-7910.md)
- [EIP-8024 pinned source](https://github.com/ethereum/EIPs/blob/34b49095ca5f7343045da279f04e7ecd1e451393/EIPS/eip-8024.md)
- [EIP-6780 pinned source](https://github.com/ethereum/EIPs/blob/688e939c4e0968f0cbc6a4e79426b498869eec19/EIPS/eip-6780.md)
- [EIP-8246 pinned source](https://github.com/ethereum/EIPs/blob/9207c6011f526bd40abd79649484a1a342585bd4/EIPS/eip-8246.md)
- [Amsterdam execution-spec gas model](https://github.com/ethereum/execution-specs/blob/0cc100eb190b64b23baba72dac0165652eaec252/src/ethereum/forks/amsterdam/vm/gas.py)

EIP-8037, EIP-8038, EIP-2780, and EIP-7928 are in Review at these revisions. Their behavior and constants can still change; this manifest prevents such changes from silently altering the proof claim. EIP-7610 is pinned because EIP-8037 explicitly requires its storage-inclusive CREATE collision rule.

## Schedule instance

The only concrete schedule instance is `GasSchedule.amsterdam`:

| Field | Value |
|---|---:|
| `cpsb` | 1,530 |
| `storageSetBytes` | 64 |
| `newAccountBytes` | 120 |
| `coldAccountAccess` | 3,000 |
| `coldStorageAccess` | 2,100 |
| `warmAccess` | 100 |
| `accountWrite` | 9,000 |
| `storageWrite` | 10,000 |
| `createAccess` | 12,000 |
| `storageClearRefund` | 11,616 |
| `accessListAddress` | 2,900 |
| `accessListStorageKey` | 2,000 |

Thus a new storage slot costs `64 * 1,530 = 97,920` state gas and a new account costs `120 * 1,530 = 183,600` state gas. The transition functions, proofs, and normative expectations are schedule-polymorphic; updating a draft parameter changes only the schedule instance.

The reservoir/SSTORE milestone exercises the six SSTORE fields (`cpsb`, `storageSetBytes`, `coldStorageAccess`, `warmAccess`, `storageWrite`, and `storageClearRefund`). The later schedule-polymorphic account-pricing and CALL/CREATE references consume the account, CREATE, and access-list fields; concrete draft parameter changes therefore remain isolated to `GasSchedule.amsterdam` and its derived expectations rather than transition or proof code.

## Relevant Nethermind surfaces

The refinement track must connect the Lean definitions to these pinned production paths:

- `src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs`
- `src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs`
- `src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs`
- `src/Nethermind/Nethermind.Core/Eip8037Constants.cs`
- `src/Nethermind/Nethermind.Core/Eip8038Constants.cs`
- `tools/Evm/T8n/T8nExecutor.cs`
- `tools/Evm/Formal/ChargeStateNdjson.cs`
- `tools/Evm/Lean/Extractor/StateGasChargeExtractor.cs`

EIP-7928 additionally requires the SSTORE access cost and the `gas_left > 2,300` sentry to succeed before the first storage read. This milestone models pricing after those preconditions; executor observation and proof of the pre-access ordering remain follow-up work. The expanded complete-EVM target also pins EIP-1153 transient storage, EIP-7778 block execution gas, EIP-7939 `CLZ`, and EIP-8024 extended stack operations because all four affect the selected Amsterdam production graph. EIP-7778 and EIP-8024 remain in Review at these revisions; EIP-1153 and EIP-7939 are Final.

The full execution-pipeline target additionally inventories the standard mainnet production graph in `PRODUCTION_SURFACES.md`; that inventory does not yet pin every task-worktree semantic source. Amsterdam remains enabled at the synthetic genesis boundary for this proof instance; because Nethermind deliberately leaves its live-mainnet activation unscheduled at the baseline commit, the result must not be described as verification of currently deployed mainnet rules.
