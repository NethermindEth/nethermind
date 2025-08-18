# Nethermind Performance Optimizations for AI-Agent Workloads

## Overview

This document outlines comprehensive performance optimizations for Nethermind to handle AI-agent workloads characterized by high-concurrency, bursty read patterns with tens of thousands of `eth_call`/minute, frequent historical `getLogs`, and deep `debug_trace*` operations during planning loops.

## Performance Goals (SLO Targets)

- **`eth_call` latency (warm state):** reduce p50/p95 by ≥30%/≥25%
- **RPC throughput:** sustain ≥2× current calls/sec on a single node before saturation
- **`getLogs` (bounded ranges):** ≥2× faster for common agent filters (topic-selective, recent blocks)
- **Tracing (`debug_trace*`):** ≥2× faster or ≥2× less peak memory on typical paths
- **CPU per imported block:** ≤85% of current baseline under identical config

## 1. EVM Hot-Path Optimizations

### 1.1 Opcode Dispatch & Inlining

**Current State:** The EVM uses function pointer dispatch through `OpCode[]` lookup table in `VirtualMachine.cs`.

**Optimization Strategy:**
```csharp
// Add aggressive inlining to critical opcodes
[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
private static EvmExceptionType InstructionMath2Param<TOperation, TTracingInst>(
    VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    where TOperation : struct, IMathOperation
    where TTracingInst : struct, IFlag
{
    // Reduce LINQ/allocations in interpreter loop
    // Use Span<T> and ref struct for stack operations
}
```

**Expected Impact:**
- **Performance Gain:** 10-15% improvement in `eth_call` p50 latency
- **Allocation Reduction:** ≥25% reduction in EVM loop allocations
- **Implementation Complexity:** Medium

### 1.2 JUMPDEST Validation & Jump Table Precomputation

**Current Implementation:** Per-step validation of jump destinations.

**Optimization Strategy:**
```csharp
public readonly struct JumpTable
{
    private readonly BitSet _validJumpDests;
    
    // SIMD-assisted scan (SSE2/AVX2) to populate markers
    public static JumpTable BuildFor(ReadOnlySpan<byte> code)
    {
        var jumpDests = new BitSet(code.Length);
        
        // Vectorized JUMPDEST scanning
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        for (int i = 0; i < code.Length; i += Vector256<byte>.Count)
        {
            var chunk = Vector256.LoadUnsafe(ref Unsafe.Add(ref codeRef, i));
            var mask = Vector256.Equals(chunk, Vector256.Create((byte)0x5B)); // JUMPDEST
            // Set bits in jumpDests based on mask
        }
        
        return new JumpTable(jumpDests);
    }
}
```

**Expected Impact:**
- **Performance Gain:** 8-15% speedup for bytecode with dense control flow
- **Memory Usage:** Minimal overhead (bitset per codehash)
- **Implementation Complexity:** Medium

### 1.3 Keccak (SHA3) Acceleration

**Current State:** Standard cryptographic implementation.

**Optimization Strategy:**
```csharp
public static class OptimizedKeccak
{
    // Ensure vectorized/unsafe path is used on supported CPUs
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void ComputeHashBytesUnsafe(ReadOnlySpan<byte> input, Span<byte> output)
    {
        // Use platform intrinsics where available
        if (Vector256.IsHardwareAccelerated)
        {
            // AVX2 optimized path
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            // SSE2 optimized path
        }
        // Fallback to standard implementation
    }
    
    // Batch small hashes where possible
    public static void ComputeHashesBatch(ReadOnlySpan<ReadOnlyMemory<byte>> inputs, Span<Hash256> outputs)
    {
        // Reduce intermediate allocations by reusing hash state
    }
}
```

**Expected Impact:**
- **Performance Gain:** 1.5-2× throughput on microbenchmarks
- **`eth_call` Improvement:** ≥5% improvement on hash-heavy traces
- **Implementation Complexity:** Medium

### 1.4 Memory/Stack Accounting Optimization

**Current Implementation:** Dynamic bounds/fee checks.

**Optimization Strategy:**
```csharp
// Replace bounds/fee checks with table-driven costs
private static readonly uint[] GasCostTable = new uint[256];
private static readonly bool[] HasMemoryExpansion = new bool[256];

static VirtualMachine()
{
    // Precompute gas costs for all opcodes
    GasCostTable[(int)Instruction.SSTORE] = GasCostOf.SStoreNetMetered;
    // ... populate all opcodes
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static long GetOpCodeGasCost(byte opcode) => GasCostTable[opcode];
```

**Expected Impact:**
- **Branch Reduction:** Remove ≥20% branches in hot path
- **Performance Gain:** 5-8% improvement in EVM execution
- **Implementation Complexity:** Small

## 2. State & Database Access Optimizations

### 2.1 RocksDB AI-Read-Heavy Preset

**Configuration Strategy:**
```csharp
public static class RocksDbAIPresets
{
    public static DbOptions GetAIReadHeavyOptions()
    {
        return new DbOptions()
        {
            // LRU block cache sizing optimized for read workloads
            BlockCache = new LRUCache(capacity: 8.GiB()),
            
            // Bloom filters for faster negative lookups
            FilterPolicy = BloomFilterPolicy.Create(bitsPerKey: 10),
            
            // Prefix extractors for efficient range queries
            PrefixExtractor = new FixedPrefixTransform(prefixLength: 8),
            
            // Universal compaction for read-heavy workloads
            CompactionStyle = CompactionStyle.Universal,
            
            // Optimized compression
            CompressionType = CompressionType.Zstd,
            CompressionLevel = 3,
            
            // Larger memtables for better write batching
            WriteBufferSize = 256.MiB(),
            MaxWriteBufferNumber = 6,
            
            // Read optimization
            MaxOpenFiles = 10000,
            MaxBackgroundCompactions = 4,
            MaxBackgroundFlushes = 2,
        };
    }
}
```

**Expected Impact:**
- **Read Amplification:** ≥20% reduction
- **`eth_call` p95:** ≥10% improvement
- **Implementation Complexity:** Small-Medium

### 2.2 Read-Only Snapshots for eth_call

**Current State:** Potential write contention during read operations.

**Optimization Strategy:**
```csharp
public class SnapshotStateReader : IStateReader
{
    private readonly Dictionary<Hash256, ISnapshot> _snapshots = new();
    private readonly object _snapshotLock = new();
    
    public ISnapshot GetReadSnapshot(Hash256 stateRoot)
    {
        lock (_snapshotLock)
        {
            if (!_snapshots.TryGetValue(stateRoot, out var snapshot))
            {
                snapshot = _database.GetSnapshot();
                _snapshots[stateRoot] = snapshot;
            }
            return snapshot;
        }
    }
    
    // Short-lived overlays for speculative calls
    public IWorldState CreateOverlay(Hash256 baseStateRoot)
    {
        var snapshot = GetReadSnapshot(baseStateRoot);
        return new OverlayWorldState(snapshot);
    }
}
```

**Expected Impact:**
- **Throughput Gain:** ≥20% under concurrency ≥64
- **Contention Reduction:** Eliminates write lock contention for reads
- **Implementation Complexity:** Medium

### 2.3 Trie Path Caching

**Current State:** Redundant RLP decode operations for trie nodes.

**Optimization Strategy:**
```csharp
public class TrieNodeCache
{
    private readonly struct CacheKey
    {
        public readonly Hash256 NodeHash;
        public readonly ReadOnlyMemory<byte> Path;
    }
    
    private readonly Dictionary<CacheKey, CachedTrieNode> _cache;
    private readonly ArrayPool<byte> _pathPool;
    
    public bool TryGetNode(Hash256 nodeHash, ReadOnlySpan<byte> path, out TrieNode node)
    {
        var key = new CacheKey { NodeHash = nodeHash, Path = path.ToArray() };
        if (_cache.TryGetValue(key, out var cached))
        {
            // Avoid redundant RLP decode; use Span<byte> to eliminate copies
            node = cached.Node;
            return true;
        }
        node = default;
        return false;
    }
}
```

**Expected Impact:**
- **Cache Hit Rate:** 60-80% for common access patterns
- **`eth_call` Speedup:** ≥15% on locality-friendly agents
- **Implementation Complexity:** Medium-Large

## 3. JSON-RPC Server & Serialization Optimizations

### 3.1 HTTP/2 + Enhanced Batching

**Current State:** HTTP/1.1 with basic batching support.

**Optimization Strategy:**
```csharp
public class OptimizedJsonRpcService
{
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly Channel<JsonRpcRequest> _requestQueue;
    
    public async Task<JsonRpcResponse[]> ProcessBatchAsync(JsonRpcRequest[] batch)
    {
        // Bounded work queues with fair scheduling
        var tasks = new Task<JsonRpcResponse>[batch.Length];
        
        for (int i = 0; i < batch.Length; i++)
        {
            tasks[i] = ProcessRequestWithBackpressure(batch[i]);
        }
        
        return await Task.WhenAll(tasks);
    }
    
    private async Task<JsonRpcResponse> ProcessRequestWithBackpressure(JsonRpcRequest request)
    {
        await _concurrencyLimiter.WaitAsync();
        try
        {
            return await ProcessRequest(request);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }
}
```

**Kestrel Configuration:**
```csharp
public static WebApplication ConfigureForAIWorkloads(this WebApplicationBuilder builder)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.Http2.MaxStreamsPerConnection = 1000;
        options.Limits.Http2.HeaderTableSize = 65536;
        options.Limits.Http2.MaxFrameSize = 32768;
        options.Limits.MaxConcurrentConnections = 10000;
        options.Limits.MaxConcurrentUpgradedConnections = 5000;
    });
    
    return builder.Build();
}
```

**Expected Impact:**
- **QPS Improvement:** ≥2× sustained QPS before saturation
- **Head-of-line Blocking:** Eliminated under mixed endpoints
- **Implementation Complexity:** Medium

### 3.2 System.Text.Json Source Generation

**Current State:** Runtime reflection-based serialization.

**Optimization Strategy:**
```csharp
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(CallOutput))]
[JsonSerializable(typeof(TransactionReceipt))]
[JsonSerializable(typeof(FilterLog[]))]
[JsonSerializable(typeof(TraceResult))]
internal partial class JsonRpcSerializationContext : JsonSerializerContext
{
}

public static class OptimizedSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = JsonRpcSerializationContext.Default
    };
    
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);
}
```

**Expected Impact:**
- **CPU Reduction:** ≥25% lower CPU in serialization hotspots
- **GC Pressure:** Reduced gen-0 collection frequency
- **Implementation Complexity:** Medium

### 3.3 WebSocket Streaming & Chunked Responses

**Current State:** Full response buffering for large results.

**Optimization Strategy:**
```csharp
public class StreamingJsonWriter
{
    private readonly PipeWriter _writer;
    private readonly Memory<byte> _buffer;
    
    public async ValueTask WriteTraceResultAsync(TraceResult trace)
    {
        // Stream large responses to avoid big object graphs
        await WriteStartObjectAsync();
        
        await WritePropertyNameAsync("result");
        await WriteStartArrayAsync();
        
        foreach (var frame in trace.Frames)
        {
            await WriteTraceFrameAsync(frame);
            
            // Apply chunked write with early flush
            if (_buffer.Length > 8192)
            {
                await FlushAsync();
            }
        }
        
        await WriteEndArrayAsync();
        await WriteEndObjectAsync();
    }
}
```

**Expected Impact:**
- **Memory Reduction:** ≥2× reduction for large trace/log responses
- **Latency:** Lower time-to-first-byte for streaming responses
- **Implementation Complexity:** Medium

## 4. Logs/Search Acceleration

### 4.1 Topic-First Pruning & Block-Range Paging

**Current State:** Sequential bloom filter checking.

**Optimization Strategy:**
```csharp
public class OptimizedLogFilter
{
    public async IAsyncEnumerable<FilterLog> GetLogsAsync(
        LogFilter filter, 
        long fromBlock, 
        long toBlock,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Early abort when topics mutually exclusive
        if (AreTopicsMutuallyExclusive(filter.Topics))
            yield break;
            
        // Parallel page scans with bounded fan-out
        const int pageSize = 1000;
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
        
        for (long blockNum = fromBlock; blockNum <= toBlock; blockNum += pageSize)
        {
            await semaphore.WaitAsync(cancellationToken);
            
            var pageTask = ProcessBlockPageAsync(
                blockNum, 
                Math.Min(blockNum + pageSize - 1, toBlock), 
                filter,
                cancellationToken);
                
            _ = pageTask.ContinueWith(_ => semaphore.Release(), 
                TaskScheduler.Default);
                
            await foreach (var log in pageTask.ConfigureAwait(false))
            {
                yield return log;
            }
        }
    }
    
    private static bool AreTopicsMutuallyExclusive(object?[]? topics)
    {
        // Implement topic exclusivity logic
        return false;
    }
}
```

**Expected Impact:**
- **Query Performance:** ≥2× faster on typical agent filters over ≤10k blocks
- **CPU Usage:** Reduced through early termination
- **Implementation Complexity:** Medium

### 4.2 Optional On-Disk Log Index (Feature Flag)

**Optimization Strategy:**
```csharp
[Flags]
public enum IndexingFeatures
{
    None = 0,
    TopicIndex = 1 << 0,
    AddressIndex = 1 << 1,
    Full = TopicIndex | AddressIndex
}

public class LogIndexBuilder
{
    private readonly IndexingFeatures _features;
    
    public async Task IndexBlockAsync(Block block)
    {
        if (!_features.HasFlag(IndexingFeatures.TopicIndex))
            return;
            
        // Build compact per-topic index segments
        var segments = new Dictionary<Hash256, List<LogLocation>>();
        
        foreach (var (receipt, txIndex) in block.Body.Transactions.Zip(block.Receipts))
        {
            foreach (var (log, logIndex) in receipt.Logs.Select((log, idx) => (log, idx)))
            {
                foreach (var topic in log.Topics)
                {
                    if (!segments.TryGetValue(topic, out var locations))
                    {
                        locations = new List<LogLocation>();
                        segments[topic] = locations;
                    }
                    
                    locations.Add(new LogLocation(block.Number, txIndex, logIndex));
                }
            }
        }
        
        // Amortize writes during block import
        await PersistSegmentsAsync(block.Number, segments);
    }
}
```

**Configuration:**
```json
{
  "JsonRpc": {
    "LogIndexing": {
      "Enabled": false,
      "Features": "TopicIndex",
      "MaxIndexedBlocks": 1000000
    }
  }
}
```

**Expected Impact:**
- **Wide Scan Performance:** ≥5-10× speedup for historical scans (when enabled)
- **Import Overhead:** <5% when enabled
- **Implementation Complexity:** Large

## 5. Tracing Efficiency Improvements

### 5.1 Slim Tracer with Selective Capture

**Current State:** Full trace data capture by default.

**Optimization Strategy:**
```csharp
[Flags]
public enum TraceOptions
{
    None = 0,
    Memory = 1 << 0,
    Stack = 1 << 1,
    Storage = 1 << 2,
    Full = Memory | Stack | Storage
}

public class SlimTracer : IBlockTracer
{
    private readonly TraceOptions _options;
    private readonly ObjectPool<TraceFrame> _framePool;
    
    public void StartOperation(int depth, long gas, Instruction opcode, int pc)
    {
        var frame = _framePool.Get();
        frame.Reset();
        frame.Depth = depth;
        frame.Gas = gas;
        frame.OpCode = opcode;
        frame.Pc = pc;
        
        // Selective capture based on options
        if (_options.HasFlag(TraceOptions.Stack))
        {
            CaptureStack(frame);
        }
        // Skip memory/storage capture if not needed
    }
    
    public void EndOperation(TraceFrame frame)
    {
        ProcessFrame(frame);
        _framePool.Return(frame); // Pool trace frames
    }
}
```

**Expected Impact:**
- **Performance:** ≥2× faster on common agent traces
- **Memory Usage:** ≥2× lower memory consumption
- **Implementation Complexity:** Medium

### 5.2 Streaming JSON Writer for Traces

**Current State:** Full DOM construction for trace output.

**Optimization Strategy:**
```csharp
public class StreamingTraceWriter
{
    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter _pipe;
    
    public async ValueTask WriteFrameAsync(TraceFrame frame)
    {
        // Zero-copy spans, buffered writer
        _writer.WriteStartObject();
        
        _writer.WriteNumber("depth"u8, frame.Depth);
        _writer.WriteNumber("gas"u8, frame.Gas);
        _writer.WriteString("opName"u8, frame.OpCode.ToString());
        
        if (frame.Stack is not null)
        {
            _writer.WritePropertyName("stack"u8);
            WriteStackArray(frame.Stack);
        }
        
        _writer.WriteEndObject();
        
        // Back-pressure on emitter
        if (_writer.BytesPending > 8192)
        {
            await _writer.FlushAsync();
        }
    }
}
```

**Expected Impact:**
- **Memory:** No OOMs on long traces
- **Latency:** Stable p95 latency for large traces
- **Implementation Complexity:** Medium

## 6. GC & Runtime Configuration

### 6.1 Server GC and LOH Optimization

**Recommended Configuration:**
```xml
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  <RetainVMGarbageCollection>true</RetainVMGarbageCollection>
  <GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>
</PropertyGroup>
```

**Runtime Settings:**
```csharp
public static class GCConfiguration
{
    public static void ConfigureForAIWorkloads()
    {
        // Configure LOH compaction cadence
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        
        // Tune GC thresholds for high-throughput scenarios
        AppContext.SetSwitch("System.GC.Concurrent", true);
        AppContext.SetSwitch("System.GC.Server", true);
    }
}
```

**Expected Impact:**
- **GC Time Reduction:** ≥20% under load
- **Latency Stability:** Reduced GC pause impact
- **Implementation Complexity:** Small

### 6.2 Tiered JIT & ReadyToRun

**Build Configuration:**
```xml
<PropertyGroup>
  <TieredCompilation>true</TieredCompilation>
  <TieredPGO>true</TieredPGO>
  <PublishReadyToRun>true</PublishReadyToRun>
  <ReadyToRunUseCrossgen2>true</ReadyToRunUseCrossgen2>
</PropertyGroup>
```

**Expected Impact:**
- **Warmup Time:** Reduced cold start latency
- **Steady State:** Fewer tiering stalls during bursts
- **Implementation Complexity:** Medium

## 7. Observability & Monitoring

### 7.1 Performance Counters & Metrics

**Implementation Strategy:**
```csharp
public static class AIWorkloadMetrics
{
    private static readonly Meter Meter = new("Nethermind.AI.Performance");
    
    // RPC metrics
    public static readonly Counter<long> EthCallCount = 
        Meter.CreateCounter<long>("eth_call_total");
    public static readonly Histogram<double> EthCallDuration = 
        Meter.CreateHistogram<double>("eth_call_duration_ms");
    
    // Queue metrics  
    public static readonly ObservableGauge<int> RequestQueueDepth =
        Meter.CreateObservableGauge<int>("request_queue_depth");
        
    // DB metrics
    public static readonly Counter<long> DbReadAmplification =
        Meter.CreateCounter<long>("db_read_amplification");
        
    // GC metrics
    public static readonly ObservableGauge<long> GCGen0Collections =
        Meter.CreateObservableGauge<long>("gc_gen0_collections");
}

public class PerformanceMonitor
{
    public void RecordEthCall(TimeSpan duration, bool success)
    {
        AIWorkloadMetrics.EthCallCount.Add(1, 
            new KeyValuePair<string, object?>("success", success));
        AIWorkloadMetrics.EthCallDuration.Record(duration.TotalMilliseconds);
    }
}
```

**Dashboard Configuration:**
```yaml
# Grafana dashboard config
dashboard:
  title: "Nethermind AI Workload Performance"
  panels:
    - title: "eth_call Latency (p50/p95)"
      targets:
        - expr: histogram_quantile(0.50, eth_call_duration_ms)
        - expr: histogram_quantile(0.95, eth_call_duration_ms)
    - title: "RPC Queue Depth"
      targets:
        - expr: request_queue_depth
```

**Expected Impact:**
- **Visibility:** Complete performance observability
- **Alerting:** Proactive regression detection
- **Implementation Complexity:** Medium

### 7.2 Automated Benchmark Harness

**BenchmarkDotNet Integration:**
```csharp
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class AIWorkloadBenchmarks
{
    private EthRpcModule _ethModule;
    private IBlockchainBridge _bridge;
    
    [GlobalSetup]
    public void Setup()
    {
        // Setup test blockchain state
        SetupTestBlockchain();
    }
    
    [Benchmark]
    [Arguments(1000)] // Number of concurrent calls
    public async Task EthCallConcurrencyTest(int concurrency)
    {
        var tasks = new Task[concurrency];
        var sw = Stopwatch.StartNew();
        
        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = SimulateEthCallAsync();
        }
        
        await Task.WhenAll(tasks);
        sw.Stop();
        
        // Record metrics for analysis
    }
    
    [Benchmark]
    public void GetLogsHistoricalScan()
    {
        var filter = new LogFilter 
        { 
            FromBlock = 1000000, 
            ToBlock = 1010000,
            Topics = new[] { KnownTopics.Transfer }
        };
        
        var logs = _bridge.GetLogs(filter);
        // Consume logs to measure performance
        foreach (var log in logs) { }
    }
}
```

**CI Integration:**
```yaml
# .github/workflows/performance.yml
name: Performance Regression Testing
on:
  pull_request:
    paths: ['src/Nethermind/**']

jobs:
  benchmark:
    runs-on: ubuntu-latest
    steps:
      - name: Run Benchmarks
        run: |
          dotnet run -c Release --project Benchmarks.slnx
          
      - name: Compare Results
        run: |
          # Compare against baseline and fail if regression > 10%
          python scripts/compare_benchmarks.py
```

**Expected Impact:**
- **Regression Prevention:** Automated performance gates
- **Baseline Tracking:** Historical performance trends
- **Implementation Complexity:** Medium

## Implementation Phases

### Phase 0: Baseline & Quick Wins (Week 1-2)
- [x] Document current SLOs and architecture
- [ ] Implement basic performance counters
- [ ] Add RocksDB AI-read-heavy preset
- [ ] Create benchmark infrastructure
- [ ] Low-risk EVM inlining improvements

### Phase 1: Core Throughput (Week 3-6)
- [ ] EVM hot-path optimizations (dispatch/JUMPDEST/keccak)
- [ ] JSON-RPC batching and HTTP/2 support
- [ ] Source-generated serializers
- [ ] Read-only snapshots for eth_call

### Phase 2: Heavy Queries (Week 7-10)
- [ ] getLogs improvements and optional indexing
- [ ] Tracing optimizations and streaming writer
- [ ] Trie path caching implementation

### Phase 3: Polish & Production (Week 11-12)
- [ ] GC/JIT profile presets
- [ ] Performance regression gates in CI
- [ ] Production monitoring and alerting
- [ ] Documentation and deployment guides

## Risk Mitigation

### Hot-Path Regression Risks
- **Mitigation:** Comprehensive microbenchmark CI + performance gates
- **Rollback Plan:** Feature flags for all optimizations
- **Monitoring:** Real-time performance dashboards

### Database Tuning Portability
- **Mitigation:** Multiple preset profiles with overrides
- **Testing:** Validate across different storage types
- **Documentation:** Clear tuning guidance per workload

### Memory Pressure Under Bursts
- **Mitigation:** Streaming + back-pressure in RPC layers
- **Monitoring:** Memory usage alerts and GC metrics
- **Circuit Breakers:** Request throttling under pressure

## Configuration Examples

### AI-Agent Optimized Configuration

```json
{
  "JsonRpc": {
    "RequestQueueLimit": 2000,
    "MaxBatchSize": 100,
    "EthModuleConcurrentInstances": 16,
    "BufferResponses": false,
    "EnablePerMethodMetrics": true,
    "LogIndexing": {
      "Enabled": true,
      "Features": "TopicIndex"
    }
  },
  "Db": {
    "Preset": "AIReadHeavy",
    "CacheIndexAndFilterBlocks": true,
    "BlockCacheSize": "8GB",
    "WriteBufferSize": "256MB",
    "MaxOpenFiles": 10000
  },
  "Evm": {
    "JumpTableCaching": true,
    "AggressiveInlining": true,
    "KeccakOptimization": true
  },
  "Sync": {
    "SnapSync": true,
    "FastBlocks": true
  }
}
```

### Production Monitoring Configuration

```json
{
  "Monitoring": {
    "MetricsEnabled": true,
    "MetricsExposePort": 9090,
    "PerformanceCounters": true,
    "TraceAllRequests": false,
    "SampleRate": 0.01,
    "Alerts": {
      "EthCallP95Threshold": "200ms",
      "QueueDepthThreshold": 1000,
      "GCPauseThreshold": "100ms"
    }
  }
}
```

This comprehensive optimization plan provides a structured approach to achieving the performance goals for AI-agent workloads while maintaining system stability and observability.