# Pinned production surfaces

This inventory describes the production behavior graph selected by the verification worktree. Nethermind commit `b2478235e71e6a7ec2a509aa0155e25d5fdfff80` is an ancestor baseline for lineage; it does not contain every added kernel or production correction. Each package's checked source manifest records the authoritative SHA-256 identities of its selected worktree sources and dependencies. Those identities must agree with the live files and pass that package's acceptance gates before its result applies. A source path appearing here is a proof target, adapter boundary, or reachability obligation; listing it or hashing it is not proof coverage. [verification-manifest.json](verification-manifest.json) records the accepted, conditional, and unaccepted package boundaries.

## Standard mainnet graph

`BlockProcessingModule` registers the standard `EthereumTransactionProcessor`, `BlockProcessor`, `BranchProcessor`, `BlockchainProcessor`, withdrawal and execution-request processors, beacon-root handler, blockhash store, and block validator. `BlockchainProcessor.Process` first decides whether the suggested block should run, then selects the synchronous branch and applies sender and EIP-7702 authority recovery to every selected block. `BranchProcessor` opens one world-state scope at the parent root (with explicit reopen points), and `WorldState.CommitTree` is called only for a successfully processed block. The asynchronous recovery queue can recover a suggested block earlier, but it is outside the synchronous theorem.

The outer composition is:

```text
suggested-block validation
  -> BlockchainProcessor eligibility and branch preparation
  -> selected-block sender and authority recovery
  -> world-state scope at parent root
  -> BlockProcessor.ProcessOne
  -> processed-header validation
  -> WorldState.CommitTree
  -> BlockchainProcessor head/publication decisions
```

Primary files:

- `src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs`
- `src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs`
- `src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs`
- `src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs`
- `src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs`
- `src/Nethermind/Nethermind.State/WorldState.cs`

## EVM dispatch and frames

`VirtualMachine.OpcodeHandlers.cs` constructs a 256-entry function-pointer table. Every slot begins as `BadInstruction`; enabled instructions replace their byte. The proof must account for all 256 bytes, not only the 154 named `Instruction` members, and must connect each enabled handler to a semantic step.

| Bytes | Family | Production implementation |
| --- | --- | --- |
| `00` | STOP | `Instructions/EvmInstructions.ControlFlow.cs` |
| `01-0B` | arithmetic | `Math1Param.cs`, `Math2Param.cs`, `Math3Param.cs` |
| `10-1E` | comparison, bitwise, shifts, CLZ | `Math1Param.cs`, `Math2Param.cs`, `Bitwise.cs`, `Shifts.cs` |
| `20` | KECCAK256 | `Crypto.cs` |
| `30-3F` | call/environment data, copy, account reads | `Environment.cs`, `CodeCopy.cs` |
| `40-4B` | block context | `Environment.cs` |
| `50-5E` | stack, memory, storage, transient storage, MCOPY | `Storage.cs` and handler mappings |
| `5F-7F` | PUSH0 and PUSH1-PUSH32 | `Stack.cs` |
| `80-9F` | DUP1-DUP16 and SWAP1-SWAP16 | `Stack.cs` |
| `A0-A4` | LOG0-LOG4 | `Stack.cs` |
| `E6-E8` | DUPN, SWAPN, EXCHANGE | `Stack.cs` |
| `F0`, `F5` | CREATE, CREATE2 | `Create.cs` |
| `F1`, `F2`, `F4`, `FA` | CALL family | `Call.cs` |
| `F3`, `FD` | RETURN, REVERT | `Call.cs`, `ControlFlow.cs` |
| `FE` | explicit INVALID | `ControlFlow.cs` |
| `FF` | SELFDESTRUCT | `ControlFlow.cs` |

Opcode activation and specialization depend on the pinned release spec. The table specializes tracing, cancellation, and EIPs including 145, 211, 2929, 3855, 4844, 5656, 7708, 7939, 8024, 8037, and 8038. The existing all-byte validity sweep in `Nethermind.Evm.Test/InvalidOpcodeTests.cs` is a differential completeness anchor, not a formal reachability proof.

`VirtualMachine.Dispatch.cs` checks code bounds, dispatches through the table, and enforces gas, stack underflow/overflow, and PUSH bounds. `VirtualMachine.cs` owns the frame loop and the success, REVERT, exceptional-halt, suspend/resume, precompile, and code-deposit paths. `VmState.cs`, `CallResult.cs`, and `EvmException.cs` carry the state and exit classifications that must be related to Lean.

## Standard precompiles

The standard registry and fork cache cover:

- ECRecover, SHA256, RIPEMD160, and Identity;
- modular exponentiation;
- BN254 add, multiply, and pairing;
- Blake2F and KZG point evaluation;
- the seven BLS12-381 operations;
- secp256r1/P256 verification.

Primary files are `Nethermind.Blockchain/EthereumPrecompileProvider.cs`, `Nethermind.Core/Precompiles/PrecompiledAddresses.cs`, `Nethermind.Specs/ReleaseSpec.cs`, and `Nethermind.Evm/CodeInfoRepository.cs`. CALL may use a direct STATICCALL precompile path; RIPEMD160 deliberately stays on the full-frame path. Both paths require an equivalence or reachability obligation. Optimism and Taiko provider extensions are outside the standard mainnet target.

## Outer transaction transition

`TransactionProcessorBase.ExecuteCore` selects the ordinary or system transaction processor. The standard user path in `TransactionProcessor.cs` orders:

1. static transaction and sender validation;
2. intrinsic-gas and fee reservation checks;
3. nonce validation and increment;
4. EIP-7702 authorization processing;
5. simple-transfer handling or `VirtualMachine.ExecuteTransaction`;
6. top-level snapshot restoration on REVERT or exception;
7. refund cap and calldata-floor settlement;
8. sender refund, priority/base/blob fee movement;
9. receipt and execution/state block-gas accounting.

The simple-transfer fast path is a non-EVM state transition and is part of the transaction theorem. An EVM exception produces a failed executed transaction, not a statically invalid transaction. EIP-8037 block gas uses the maximum of cumulative execution and state dimensions, while cumulative receipt gas remains the sum paid by transactions.

System transactions bypass ordinary gas purchase, nonce, and fee processing. Beacon-root and execution-request calls therefore need separate relations and must not be folded into the user-transaction receipt semantics.

## Block transition and CommitTree boundary

Inside `BlockProcessor.ProcessOne`, production order is:

1. prepare block-access-list processing, select the system-contract handler, apply the historical DAO transition when applicable, and clone the header for processing;
2. start receipt tracing, install the block execution context, and set up block-access-list state;
3. apply the beacon-root system call and blockhash-history change, then stage the pre-block state;
4. fold user transactions and stage their state changes;
5. compute blob gas and start or complete receipt bloom/root calculation;
6. apply rewards and withdrawals, then stage their state changes;
7. derive execution requests from receipts and apply the request-system calls;
8. finish receipt tracing and commit storage roots;
9. capture account changes, compute the state root, join any background receipt bloom/root calculation, and set the block access list;
10. calculate the processed header hash, validate it against the proposed block, copy validated execution artifacts back, optionally expose receipts, and return the processed block and receipts.

After `ProcessOne` returns, `BranchProcessor` records inclusion-list satisfaction as a signal, invokes the cache-prewarm hook, and then calls `WorldState.CommitTree` for the successful scoped state. An unsatisfied inclusion list is not a rejection: the block is committed normally and `BlockchainProcessor` publishes the unsatisfied notification afterward. Interim `WorldState.Commit(..., commitRoots: false)` calls stage journaled state before this outer invocation boundary. For Amsterdam's EIP-8037 path, `WorldState.Commit` reaps EIP-161-empty accounts before committing persistent storage; the processing inventory pins that source order but does not establish persistence semantics. A failed block does not reach its `CommitTree`; in a multi-block branch the already successful prefix has already invoked `CommitTree` once per block before processing stops. `BranchProcessor` can discard the current scoped attempt and retry a BAL failure sequentially before that block's invocation. A BAL with only zero-valued current or prior storage writes over an inherited storage collision cannot establish whether it cleared the final live parent slot, so it fails closed to that sequential retry. In ordinary EVM execution, a code-less CREATE target cannot first modify its own storage; a code or delegation target already has a code collision. The direct regression preserves the conservative behavior for malformed or host-wrapper access lists. The branch uses one outer state scope, reopening it only at the retry and periodic checkpoint boundaries, resetting state after each success, and disposing the final scope on exit. Database crash durability and persistence correctness remain outside this source-level inventory. The parallel block-access-list executor can discard its scope and replay sequentially; it needs an equivalence theorem before it is covered by the final source-level claim.

Outside that state transition, synchronous `BlockchainProcessor.Process` rejects missing-parent inputs, skips non-improving branches unless forced, prepares the branch, translates `InvalidBlockException` into invalid-block bookkeeping, propagates total difficulty, optionally updates the main chain/head, and optionally marks the branch processed. These decisions are part of the complete block-processing refinement target. Queue admission, worker scheduling, metrics, logging, and notification timing are operational observations and are excluded unless they can change the consensus-visible synchronous result.

Other primary surfaces include `Nethermind.Consensus/Withdrawals/WithdrawalProcessor.cs`, `Nethermind.Consensus/ExecutionRequests/ExecutionRequestsProcessor.cs`, `Nethermind.Blockchain/BeaconBlockRoot/BeaconBlockRootHandler.cs`, `Nethermind.Blockchain/Blocks/BlockhashStore.cs`, `Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs`, `Nethermind.Evm/TransactionProcessing/GasConsumed.cs`, and `Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs`.

## Composition boundary

The proof is deliberately layered:

```text
restricted pure kernels
  -> opcode and adapter simulation
  -> bounded frame-loop simulation
  -> outer transaction refinement
  -> sequential block-fold refinement
  -> standard graph reachability
  -> parallel-to-sequential equivalence
```

No later testing layer can substitute for an earlier refinement theorem. In particular, t8n state/root agreement does not establish source refinement, and map equality does not establish trie-root correctness without the RLP, hashing, trie, and persistence obligations identified in `VERIFICATION_SCOPE.md`.
