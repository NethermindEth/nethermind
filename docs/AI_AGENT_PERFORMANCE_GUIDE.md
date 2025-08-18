# AI-Agent Performance Tuning Guide

This guide provides practical recommendations for optimizing Nethermind for AI-agent workloads characterized by high-QPS `eth_call`, `getLogs`, and `debug_trace*` operations.

## Quick Start Configuration

### 1. Enable AI-Agent Optimizations

Add to your configuration file:

```json
{
  "JsonRpc": {
    "EnableAIAgentOptimizations": true,
    "RequestQueueLimit": 2000,
    "EthModuleConcurrentInstances": 16,
    "AIAgentMaxQueueDepth": 2000,
    "EnableStreamingResponses": true,
    "EnablePerMethodMetrics": true
  },
  "Db": {
    "Preset": "AIReadHeavy"
  }
}
```

### 2. Set Database Tuning

Configure your database for read-heavy workloads:

```bash
# Set RocksDB tuning type via environment or config
export NETHERMIND_DB_TUNE_TYPE="AIReadHeavy"
```

### 3. Runtime Optimizations

Configure .NET runtime for optimal performance:

```bash
# GC Settings
export DOTNET_gcServer=1
export DOTNET_gcConcurrent=1
export DOTNET_GCRetainVM=1

# JIT Settings  
export DOTNET_TieredCompilation=1
export DOTNET_TieredPGO=1
```

## Detailed Configuration Options

### Database Optimization

The `AIReadHeavy` tuning preset configures RocksDB with:

- **Large Block Cache**: 8GB cache with pinned L0 filters
- **Bloom Filters**: 10-bit bloom filters for fast negative lookups
- **Universal Compaction**: Optimized for read-heavy workloads
- **Compression**: ZSTD level 3 for balanced compression/speed
- **Background Jobs**: Tuned for concurrent read operations

### JsonRPC Performance Settings

| Setting | Default | AI-Optimized | Description |
|---------|---------|--------------|-------------|
| `RequestQueueLimit` | 500 | 2000 | Max concurrent requests in queue |
| `EthModuleConcurrentInstances` | CPU count | 16 | Parallel eth_call instances |
| `MaxBatchSize` | 1024 | 100 | Optimal batch size for AI agents |
| `BufferResponses` | false | false | Keep chunked for large responses |
| `EnablePerMethodMetrics` | false | true | Track performance per RPC method |

### Memory and GC Tuning

For AI-agent workloads, configure:

```xml
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  <GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>
</PropertyGroup>
```

## Performance Monitoring

### Built-in Metrics

With `EnableAIAgentOptimizations=true`, monitor these metrics:

```csharp
// Request counts
AIWorkloadMetrics.EthCallCount
AIWorkloadMetrics.GetLogsCount  
AIWorkloadMetrics.DebugTraceCount

// Performance metrics
AIWorkloadMetrics.AverageEthCallDurationMs
AIWorkloadMetrics.AverageGetLogsDurationMs
AIWorkloadMetrics.RequestErrors
AIWorkloadMetrics.RequestsThrottled
```

### Recommended Dashboards

Create monitoring dashboards tracking:

1. **RPC Performance**
   - Request rate (calls/second)
   - Average response time (ms)
   - Queue depth
   - Error rate (%)

2. **Database Performance**
   - Read amplification
   - Cache hit rate
   - Disk I/O patterns

3. **System Resources**
   - CPU utilization
   - Memory usage
   - GC frequency and duration

## Benchmarking

### Running Performance Tests

Test your configuration with included benchmarks:

```bash
cd src/Nethermind
dotnet run -c Release --project Nethermind.JsonRpc.Benchmark

# Run specific AI-agent scenarios
dotnet run -c Release --project Nethermind.JsonRpc.Benchmark -- --filter "*EthCallBatch*"
dotnet run -c Release --project Nethermind.JsonRpc.Benchmark -- --filter "*GetLogsHistoricalRange*"
```

### Baseline Measurements

Before optimization, capture baseline metrics:

```bash
# Record current performance
dotnet run -c Release --project Nethermind.JsonRpc.Benchmark -- --exporters json
```

### Target Performance Goals

| Metric | Baseline | Target | Improvement |
|--------|----------|--------|-------------|
| eth_call p50 latency | TBD | -30% | 30% reduction |
| eth_call p95 latency | TBD | -25% | 25% reduction |
| RPC throughput | TBD | +100% | 2× improvement |
| getLogs (10k blocks) | TBD | +100% | 2× faster |
| debug_trace memory | TBD | -50% | 50% reduction |

## Troubleshooting

### Common Issues

**1. High Memory Usage**
- Enable streaming responses: `"EnableStreamingResponses": true`
- Reduce batch sizes: `"MaxBatchSize": 50`
- Monitor GC settings

**2. Request Timeouts**  
- Increase timeout: `"Timeout": 30000`
- Check queue depth: Monitor `AIWorkloadMetrics.RequestsThrottled`
- Scale concurrent instances: Increase `EthModuleConcurrentInstances`

**3. Database Performance**
- Verify AIReadHeavy preset is active
- Monitor read amplification metrics
- Check disk I/O capacity

**4. CPU Bottlenecks**
- Verify server GC is enabled
- Check tiered compilation settings
- Monitor thread pool utilization

### Performance Regression Detection

Set up alerts for:

```yaml
# Example alerting thresholds
alerts:
  - metric: "eth_call_p95_duration_ms"
    threshold: 200
    action: "investigate_performance_regression"
  
  - metric: "request_queue_depth"
    threshold: 1500
    action: "scale_resources"
    
  - metric: "gc_gen2_collections_per_minute"
    threshold: 10
    action: "check_memory_pressure"
```

## Advanced Optimizations

### Custom RocksDB Tuning

For specific workloads, customize database settings:

```csharp
// Override default AIReadHeavy settings
var customOptions = new Dictionary<string, string>
{
    { "table_factory.block_cache_size", 16.GiB().ToString() }, // Larger cache
    { "max_open_files", "20000" }, // More open files
    { "level0_file_num_compaction_trigger", "1" } // Aggressive compaction
};
```

### Application-Level Optimizations

1. **Request Batching**: Group related calls into single batches
2. **Caching**: Implement application-level caching for repeated queries
3. **Connection Pooling**: Use persistent HTTP connections
4. **Query Optimization**: Use specific block numbers instead of "latest"

### State Snapshot Optimization

For read-heavy workloads, consider:

```json
{
  "Sync": {
    "SnapSync": true,
    "FastBlocks": true
  },
  "State": {
    "ReadOnlySnapshots": true  // Future optimization
  }
}
```

## Production Deployment

### Resource Requirements

**Minimum Requirements for AI Workloads:**
- CPU: 8+ cores with high single-thread performance
- Memory: 32GB+ RAM (16GB+ for RocksDB cache)
- Storage: NVMe SSD with high IOPS
- Network: Low-latency connection

**Recommended Configuration:**
- CPU: 16+ cores, 3.0GHz+ base clock
- Memory: 64GB+ RAM (allows 32GB+ for caching)
- Storage: Enterprise NVMe SSD, 100k+ IOPS
- Network: 10Gbps+ with <1ms latency

### Scaling Strategies

1. **Vertical Scaling**: Increase resources on single node
2. **Load Balancing**: Distribute requests across multiple nodes
3. **Read Replicas**: Use separate nodes for read-only operations
4. **Caching Layer**: Add Redis/Memcached for frequent queries

### Maintenance

Regular performance maintenance:

1. **Monitor Metrics**: Track performance trends
2. **Database Compaction**: Schedule during low-traffic periods  
3. **Log Analysis**: Review slow query logs
4. **Resource Planning**: Predict capacity needs

## References

- [PERFORMANCE_OPTIMIZATIONS.md](../PERFORMANCE_OPTIMIZATIONS.md) - Comprehensive technical guide
- [RocksDB Tuning Guide](https://github.com/facebook/rocksdb/wiki/RocksDB-Tuning-Guide)
- [.NET GC Performance Guidelines](https://docs.microsoft.com/en-us/dotnet/standard/garbage-collection/performance)
- [ASP.NET Core Performance Best Practices](https://docs.microsoft.com/en-us/aspnet/core/performance/performance-best-practices)