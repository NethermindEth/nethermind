# Nethermind.OpcodeTracing.Plugin

The Opcode Tracing Plugin enables tracing of EVM opcode usage across configurable block ranges with flexible output modes. It supports both real-time tracing during block processing and retrospective analysis of historical blocks.

## Features

- **Dual Tracing Modes**:
  - **RealTime**: Traces opcodes as blocks are processed during sync or as new blocks arrive (<5% overhead)
  - **Retrospective**: Analyzes historical blocks from the database without impacting live sync

- **Flexible Block Range Configuration**:
  - Explicit range: `--OpcodeTracing.StartBlock 100 --OpcodeTracing.EndBlock 200`
  - Recent N blocks: `--OpcodeTracing.Blocks 100`

- **Comprehensive JSON Output**: Single JSON file per trace with metadata and aggregated opcode counts

## Architecture

### Core Components

- **OpcodeTracingPlugin**: Plugin entry point implementing `INethermindPlugin`
- **OpcodeTracingModule**: Autofac module for dependency injection
- **IOpcodeTracingConfig / OpcodeTracingConfig**: Strongly-typed configuration
- **OpcodeTraceRecorder**: Orchestrates tracing operations across block ranges
- **OpcodeCounter**: Thread-safe opcode accumulator
- **TraceOutputWriter**: Writes JSON output files

### Tracing Pipeline

- **OpcodeCountingTxTracer**: Per-transaction tracer capturing opcode execution
- **OpcodeBlockTracer**: Per-block tracer aggregating transaction-level counts
- **RealTimeTracer**: Hooks into live block processing
- **RetrospectiveTracer**: Reads historical blocks from BlockTree

### Utilities

- **BlockRangeValidator**: Configuration validation with detailed error messages
- **DirectoryHelper**: Output directory creation and write validation
- **TracingProgress**: Progress tracking with periodic logging (every 1000 blocks)

## Building the Plugin

```bash
# From the repository root
cd src/Nethermind
dotnet build Nethermind.OpcodeTracing.Plugin/Nethermind.OpcodeTracing.Plugin.csproj -c Release
```

The resulting assembly will be located at:
`artifacts/bin/Nethermind.OpcodeTracing.Plugin/release/Nethermind.OpcodeTracing.Plugin.dll`

## Configuration

### Required Configuration

Enable the plugin and specify a block range:

```bash
# Real-time mode with explicit range
dotnet run --project Nethermind.Runner -- \\
  --config mainnet \\
  --OpcodeTracing.Enabled true \\
  --OpcodeTracing.StartBlock 18000000 \\
  --OpcodeTracing.EndBlock 18001000 \\
  --OpcodeTracing.Mode RealTime

# Retrospective mode with recent N blocks
dotnet run --project Nethermind.Runner -- \\
  --config mainnet \\
  --OpcodeTracing.Enabled true \\
  --OpcodeTracing.Blocks 100 \\
  --OpcodeTracing.Mode Retrospective
```

### Configuration Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `Enabled` | bool | false | Enable the plugin |
| `OutputDirectory` | string | "traces/opcodes" | Output directory for JSON files |
| `StartBlock` | long? | null | First block number (inclusive) |
| `EndBlock` | long? | null | Last block number (inclusive) |
| `Blocks` | long? | null | Number of recent blocks to trace |
| `Mode` | string | "RealTime" | Tracing mode: RealTime or Retrospective |
| `MaxDegreeOfParallelism` | int | 0 | Parallel processing limit (0 = auto) |

### JSON Configuration

```json
{
  "OpcodeTracing": {
    "Enabled": true,
    "StartBlock": 18000000,
    "EndBlock": 18001000,
    "OutputDirectory": "/data/traces",
    "Mode": "RealTime",
    "MaxDegreeOfParallelism": 0
  }
}
```

## Output Format

### File Naming

Output files follow the pattern:
```
opcode-trace-{startBlock}-{endBlock}-{timestamp}.json
```

Example: `opcode-trace-18000000-18001000-20251204143045.json`

### JSON Structure

```json
{
  "metadata": {
    "startBlock": 18000000,
    "endBlock": 18001000,
    "mode": "RealTime",
    "timestamp": "2025-12-04T14:30:45.123Z",
    "duration": 123456,
    "completionStatus": "complete"
  },
  "opcodeCounts": {
    "ADD": 1523894,
    "MUL": 892341,
    "SLOAD": 456789,
    "SSTORE": 321654,
    "CALL": 89234,
    "PUSH1": 2345678,
    "0xfe": 12
  }
}
```

### Metadata Fields

- **startBlock** (required): First block traced
- **endBlock** (required): Last block traced
- **mode** (optional): Tracing mode used
- **timestamp** (optional): Completion time (UTC)
- **duration** (optional): Elapsed milliseconds
- **completionStatus** (optional): "complete", "partial", or "error"
- **warnings** (optional): Array of warning messages

### Opcode Counts

Dictionary mapping opcode names to occurrence counts:
- Known opcodes: Uppercase names (e.g., "ADD", "SLOAD")
- Unknown opcodes: Hex format (e.g., "0xfe")

## Usage Examples

### Trace Historical Blocks

```bash
# Trace blocks 17,000,000 to 17,001,000 retrospectively
dotnet run --project Nethermind.Runner -- \\
  --config mainnet \\
  --OpcodeTracing.Enabled true \\
  --OpcodeTracing.StartBlock 17000000 \\
  --OpcodeTracing.EndBlock 17001000 \\
  --OpcodeTracing.Mode Retrospective \\
  --OpcodeTracing.OutputDirectory ./traces
```

### Trace During Live Sync

```bash
# Trace blocks 18,500,000 to 18,501,000 as they arrive
dotnet run --project Nethermind.Runner -- \\
  --config mainnet \\
  --OpcodeTracing.Enabled true \\
  --OpcodeTracing.StartBlock 18500000 \\
  --OpcodeTracing.EndBlock 18501000 \\
  --OpcodeTracing.Mode RealTime \\
  --OpcodeTracing.OutputDirectory ./traces
```

### Trace Recent Blocks

```bash
# Trace the most recent 100 blocks
dotnet run --project Nethermind.Runner -- \\
  --config mainnet \\
  --OpcodeTracing.Enabled true \\
  --OpcodeTracing.Blocks 100 \\
  --OpcodeTracing.Mode Retrospective
```

## Validation and Error Handling

### Configuration Validation

The plugin validates configuration on startup:
- **VR-001**: StartBlock must be ≤ EndBlock
- **VR-002**: Warns when both explicit range and Blocks parameter specified
- **VR-003**: At least one range specification required
- **VR-004**: EndBlock must not exceed current chain tip
- **VR-005**: Output directory must be writable

### Error Detection

Errors are detected within 5 seconds of startup and logged with clear messages:
- "Invalid range: StartBlock > EndBlock"
- "No block range specified"
- "Output directory is not writable"

### Partial Completion

If tracing is interrupted (Ctrl+C, crash, etc.):
- Accumulated data is written to JSON file
- `completionStatus` set to "partial"
- `endBlock` reflects actual last processed block

## Performance Characteristics

### Real-Time Mode
- Overhead: <5% impact on block processing
- Best for: Continuous monitoring, new block analysis
- Processing: ~10 minutes for 1000 blocks (varies by network)

### Retrospective Mode
- Overhead: No impact on live sync
- Best for: Historical analysis, research
- Processing: Faster than real-time (direct database access)

### Memory Footprint
- Base: ~5-20 KB
- Per tracer: ~2 KB
- Output file: ~1-10 KB per trace

## Progress Tracking

Progress is logged every 1000 blocks:
```
[Info] Opcode tracing progress: block 17500000 (50.00% complete)
[Info] Real-time tracing progress: block 18500500 (50.00% complete)
```

## Limitations

1. **Plugin Isolation**: Cannot modify Nethermind core code
2. **Block Availability**: Retrospective mode requires synced blocks
3. **Real-Time Overhead**: Adds <5% to block processing
4. **Concurrent Instances**: Unique filenames prevent conflicts

## Implementation Notes

### Technical Decisions

- **OpcodeLabelCache**: Pre-built 256-element string array for O(1) opcode name lookups
- **Lock-Free Counting**: Per-transaction tracers use thread-local arrays, aggregated after block completion
- **Atomic Accumulation**: Global counter uses `Interlocked.Increment` for thread safety
- **Minimal Tracer Flags**: Only `IsTracingInstructions = true` to avoid unnecessary callbacks

### Constitution Compliance

- **Plugin Isolation**: All code in plugin directory, no core modifications
- **Configuration Conventions**: Uses Nethermind's `IConfig` pattern with `[ConfigItem]` attributes
- **Code Style**: C# 13, file-scoped namespaces, nullable reference types, SPDX headers
- **Dependency Injection**: Autofac module for service registration

## Troubleshooting

### Plugin Not Loading
- Verify `--OpcodeTracing.Enabled true` is set
- Check Nethermind logs for initialization messages

### Invalid Block Range Error
- Ensure `StartBlock` ≤ `EndBlock`
- Both values must be non-negative

### Blocks Not Synced Error
- Wait for Nethermind to sync to requested block range
- Use `--OpcodeTracing.Blocks` for recent blocks

### Output Directory Not Writable
- Verify path exists and has write permissions
- Use absolute paths to avoid ambiguity

## Additional Resources

- [Nethermind Plugin Documentation](https://docs.nethermind.io/developers/plugins/)
- [Specification](../../../specs/001-opcode-tracing/spec.md)
- [Implementation Plan](../../../specs/001-opcode-tracing/plan.md)
- [Quickstart Guide](../../../specs/001-opcode-tracing/quickstart.md)

## License

LGPL-3.0-only - See SPDX headers in source files
