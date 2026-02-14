# Intrinsic Gas Double Calculation Benchmark Summary

**Date:** 2026-02-13  
**Issue:** [#9260](https://github.com/NethermindEth/nethermind/issues/9260)  
**Problem:** Intrinsic gas is calculated twice per transaction - once during validation and once during execution

## Code Analysis

### 1. The Intrinsic Gas Calculator

**Location:** `src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs`

The intrinsic gas calculation consists of:
- **Base transaction cost:** 21,000 gas (constant)
- **Data cost:** Iterates over ALL calldata bytes counting zeros vs non-zeros
  - Zero bytes: 4 gas each
  - Non-zero bytes: 16 gas each (or 68 gas pre-EIP-2028)
- **Create cost:** +32,000 gas if contract creation (EIP-2)
- **Access list cost:** Variable based on addresses and storage keys (EIP-2930)
- **Authorization list cost:** 25,000 gas per authorization (EIP-7702)
- **Floor gas calculation:** For EIP-7623 (iterates calldata again)

**Critical observation:** The most expensive part is the `CalculateTokensInCallData()` method which iterates over every byte of the calldata using `data.CountZeros()`. For large calldata (10KB+), this is a non-trivial operation.

### 2. First Call - Validation

**Location:** `src/Nethermind/Nethermind.Consensus/Validators/TxValidator.cs` (line ~127)

```csharp
public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
{
    // This is unnecessarily calculated twice - at validation and execution times.
    EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, releaseSpec);
    return transaction.GasLimit < intrinsicGas.MinimalGas
        ? TxErrorMessages.IntrinsicGasTooLow
        : ValidationResult.Success;
}
```

**Purpose:** Check if the transaction provides enough gas to cover intrinsic costs.

### 3. Second Call - Execution

**Location:** `src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs` (line ~192)

```csharp
protected virtual TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)
{
    BlockHeader header = VirtualMachine.BlockExecutionContext.Header;
    IReleaseSpec spec = GetSpec(header);
    
    // ... setup code ...
    
    IntrinsicGas<TGasPolicy> intrinsicGas = CalculateIntrinsicGas(tx, spec);
    if (!(result = ValidateStatic(tx, header, spec, opts, in intrinsicGas))) return result;
    
    // ... rest of execution ...
}
```

**Purpose:** Calculate intrinsic gas again for execution (deducted from gas limit).

### 4. Why This Matters

**Frequency:** Every single transaction that enters the system goes through BOTH calls:
1. Mempool validation (TxValidator)
2. Block execution (TransactionProcessor)

**Cost scales with:** 
- Calldata size (O(n) where n = calldata bytes)
- Transaction complexity (access lists, authorization lists)

**Theoretical overhead:**
- Small tx (10 bytes calldata): ~50-100ns per call = **100-200ns wasted**
- Medium tx (1KB calldata): ~500-1000ns per call = **1-2μs wasted**
- Large tx (10KB calldata): ~5-10μs per call = **10-20μs wasted**
- Very large tx (100KB calldata): ~50-100μs per call = **100-200μs wasted**

**Block-level impact:**
- If a block has 100 transactions averaging 1KB calldata each: **100-200μs wasted per block**
- High-throughput blocks with hundreds of transactions: **milliseconds wasted**

## Benchmark Design

### Benchmark File Created

**Location:** `src/Nethermind/Nethermind.Evm.Benchmark/IntrinsicGasBenchmarks.cs`

### Benchmark Categories

#### 1. **SingleCall** - Baseline measurement
Measures the cost of a single intrinsic gas calculation for different transaction types:
- Legacy transactions (small/large calldata)
- EIP-1559 transactions (small/large calldata)
- EIP-2930 transactions (with access lists)
- EIP-4844 blob transactions
- Contract creation transactions (small/large)

#### 2. **DoubleCall** - Current behavior overhead
Simulates the actual double calculation:
- Calls `IntrinsicGasCalculator.Calculate()` twice (validation + execution)
- Measures total time for both calls
- Direct evidence of wasted computation

#### 3. **Scaling** - Data size impact
Parametric benchmark testing calldata sizes: 0, 10, 100, 1000, 10000, 100000 bytes
- Shows how cost scales with transaction size
- Identifies the linear relationship with calldata size

#### 4. **Baseline** - Context comparisons
- `CountZeros()` operation in isolation (core of the calculation)
- Simple arithmetic operations
- Helps understand what else could be done with the same CPU time

### Key Benchmark Features

1. **Memory diagnostics enabled** - tracks allocation overhead
2. **Grouped by category** - easy to analyze results
3. **Realistic test data:**
   - Mixed zero/non-zero calldata (realistic patterns)
   - Deterministic random seed (42) for reproducibility
   - Access lists with realistic structure
   - Proper transaction signing

## Build Status

✅ **BUILD SUCCESSFUL**

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:49.45
```

**Output:** `src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll`

### Build Configuration
- Configuration: Release
- SDK: .NET 10.0.100-rc.2.25502.107 (global.json backed up)
- Compiler flags: `-p:UseSharedCompilation=false` (memory optimization)

## How to Run Benchmarks

### Run all benchmarks
```bash
cd /root/projects/nethermind
/usr/share/dotnet/dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark
```

### Run specific category
```bash
# Single call benchmarks only
/usr/share/dotnet/dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark -- --filter *SingleCall*

# Double call overhead
/usr/share/dotnet/dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark -- --filter *DoubleCall*

# Scaling analysis
/usr/share/dotnet/dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark -- --filter *Scaling*
```

### Export results
```bash
/usr/share/dotnet/dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark -- --exporters json,html
```

## Expected Results Analysis

### What to look for:

1. **Single vs Double ratio:** 
   - Should be approximately 2x for DoubleCall vs SingleCall
   - Any overhead beyond 2x indicates additional costs (memory allocation, cache misses)

2. **Scaling linearity:**
   - Plot results from the Scaling benchmark
   - Should show linear growth with calldata size
   - Intercept = fixed costs (base tx + overhead)
   - Slope = per-byte cost

3. **Percentage of transaction processing:**
   - Compare intrinsic gas calculation time to typical transaction execution
   - For simple transfers, intrinsic gas might be 5-10% of total time
   - For large calldata with minimal execution, could be 20-30%

4. **Memory allocations:**
   - Should be minimal or zero (calculations use spans, not allocations)
   - Any allocations indicate optimization opportunities

## Recommended Next Steps

### 1. Run the benchmarks
```bash
cd /root/projects/nethermind
/usr/share/dotnet/dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark -- --filter IntrinsicGas*
```

**Note:** Running benchmarks takes 10-30 minutes. They run each test multiple times for statistical significance.

### 2. Analyze results
- Look at Mean, StdDev, and Median times
- Check memory allocations (Gen0/Gen1/Gen2 collections)
- Compare DoubleCall vs SingleCall categories
- Examine scaling behavior

### 3. Document findings
Create a report with:
- Actual overhead numbers (μs per transaction type)
- Block-level impact calculations
- Graphs showing scaling with calldata size
- Comparison to total transaction processing time

### 4. Propose optimization

**Option A:** Cache intrinsic gas in transaction object
```csharp
class Transaction {
    private IntrinsicGas? _cachedIntrinsicGas;
    public IntrinsicGas GetIntrinsicGas(IReleaseSpec spec) {
        return _cachedIntrinsicGas ??= IntrinsicGasCalculator.Calculate(this, spec);
    }
}
```

**Option B:** Pass intrinsic gas from validation to execution
- Modify `ValidationResult` to include intrinsic gas
- TransactionProcessor receives it instead of recalculating

**Option C:** Lazy calculation in transaction object
- Calculate once on first access
- Store in transaction state

### 5. Submit PR with evidence

Include:
- Benchmark results (before optimization)
- Implementation of chosen optimization
- New benchmark results (after optimization)
- Improvement percentage
- This analysis document

## Files Modified/Created

1. ✅ **Created:** `src/Nethermind/Nethermind.Evm.Benchmark/IntrinsicGasBenchmarks.cs`
2. ✅ **Backed up:** `global.json` → `global.json.bak`
3. ✅ **Created:** This summary document

## Conclusion

The benchmark infrastructure is ready to **prove the overhead with real numbers**. The double calculation of intrinsic gas is confirmed in the code, and the benchmark will quantify:

- Exact time cost per transaction type
- How cost scales with calldata size  
- Percentage of total transaction processing time
- Memory overhead (if any)

This data will provide strong justification for the optimization PR and help choose the best implementation approach.

---

**Next action:** Run the benchmarks and collect results to include in the GitHub issue and PR.
