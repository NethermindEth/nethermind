# EVM Module Context

Knowledge specific to Nethermind.Evm, instruction handlers, and precompiles.

## Stack operation indexing

- EVM immediate operand instructions (EIP-8024) are **0-indexed** but stack operations
  are **1-indexed**. SWAPN's operand `n` means "swap with (n+1)th from top" — must pass
  `depth + 1` to `stack.Swap()`. EXCHANGE uses +2/+1 offsets. DUPN is 0-based and does
  NOT need the +1. The asymmetry between Dup and Swap is the trap.

## Memory

- EVM memory size limit is `int.MaxValue - WordSize + 1`, not `int.MaxValue`.
  Word alignment adds up to 31 bytes, and .NET arrays are int-indexed.
- `CheckMemoryAccessViolation` output parameters (`newLength`, `isViolation`) must be used
  consistently across all `TrySave*` methods — don't manually recompute what the helper returns.

## Gas policy

- Intrinsic gas calculation lives in `IGasPolicy<TSelf>` static interface methods.
  `IntrinsicGasCalculator` is a thin facade. L2 gas policies extend `IGasPolicy<TSelf>`.

## Precompile naming

- Standard names follow cryptographic convention: `ECRecover`, `BN254`, `SecP256k1`,
  `SecP256r1`, `KzgPointEvaluation`
- Use standard names in all new code — required for zkEVM compatibility
- The naming uses uppercase at abbreviation boundaries (EC, BN, Sec, Kzg)

## Performance

- `ParallelUnbalancedWork.For` (work-stealing via atomic counter) over `Parallel.For`
  (range partitioning) for variable-cost iterations. Bounds threads via
  `RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16`.
- Vector-sized stack variables (`Vector512<byte>`, `Vector256<byte>`) replace `stackalloc`
  in hot serialization paths to avoid GS cookie overhead (~2 extra writes per call).
