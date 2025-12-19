# Nethermind.OpcodeTracing.Plugin

The Opcode Tracing Plugin enables tracing of EVM opcode usage across configurable block ranges. It supports both real-time tracing during block processing and retrospective analysis of historical blocks.

## Features

- **Dual Tracing Modes**:
  - **RealTime**: Traces opcodes as blocks are processed at the chain tip AFTER sync completes
  - **Retrospective**: Analyzes transaction bytecode from historical blocks in the database

- **Flexible Block Range Configuration**:
  - Explicit range: `--OpcodeTracing.StartBlock 100 --OpcodeTracing.EndBlock 200`
  - Recent N blocks: `--OpcodeTracing.Blocks 100`

- **Comprehensive JSON Output**:
  - **Retrospective mode**: Single JSON file per trace with metadata and aggregated opcode counts
  - **RealTime mode**: Dual output - per-block JSON files + cumulative JSON file with running totals

- **Parallel Processing for Retrospective Mode**:
  - Traces blocks in parallel using the `MaxDegreeOfParallelism` parameter

## Understanding the Tracing Modes

### RealTime Mode

**Important**: RealTime mode traces opcodes from EVM execution of **new blocks at the chain tip**. It requires the node to be **fully synced** before tracing begins.

**Why?** During initial sync, blocks are downloaded and stored without executing the EVM - the state is reconstructed from downloaded trie nodes instead. Only after sync completes do new blocks get processed through the EVM, which is when RealTime opcode tracing captures execution data.

**Output**: RealTime mode produces **dual JSON output**:

1. **Per-block files** (`opcode-trace-block-{blockNumber}.json`): Written immediately after each block is processed, containing opcodes for that single block
2. **Cumulative file** (`opcode-trace-all-{sessionId}.json`): Updated after each block with aggregated counts from all blocks processed in the session

**Block Range Behavior**:
When `StartBlock` and `EndBlock` are configured:

- Only blocks within the configured range are traced
- After `EndBlock` is reached, tracing **stops** and the cumulative file is finalized with `completionStatus="complete"`
- Blocks after `EndBlock` are **not** traced

When `Blocks` is configured:

- All blocks after the current chain tip **are traced** (i.e. the next `Blocks` blocks)
- Blocks before the current chain tip are **not** traced
- The plugin calculates the effective start and end blocks based on the current chain tip and the `Blocks` parameter
- After the effective end block is reached, tracer starts to create per-block files and update the cumulative file with the aggregated counts from all traced blocks

**Best for**:

- Monitoring opcode usage on production nodes
- Analyzing newly mined blocks in real-time

**Not suitable for**:

- Analyzing historical blocks during sync
- Testing on a node that hasn't finished syncing
- When performance is important, since the mode should have minimal impact on block processing

### Retrospective Mode

Retrospective mode analyzes transaction bytecode (input data) from historical blocks stored in the database. It performs **static analysis** of opcodes present in transaction data, not actual EVM execution.

**Best for**:

- Research on historical blocks after sync

**Not suitable for**:

- If need to analyze historical blocks during sync

**Note**: This mode counts opcodes found in transaction data bytes, which may be different from actual executed opcodes. For true execution tracing of historical blocks, you would need to replay transactions (not currently implemented).

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
  --config volta \\
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

### File Namin

**Retrospective Mode**:

```
opcode-trace-{startBlock}-{endBlock}-{timestamp}.json
```
Example: `opcode-trace-18000000-18001000-20251204143045.json`

**RealTime Mode** (dual output):

- Per-block files: `opcode-trace-block-{blockNumber}.json`
- Cumulative file: `opcode-trace-all-{sessionId}.json` (sessionId format: `yyyyMMddHHmmss`)

Example:
```
opcode-trace-block-18000000.json
opcode-trace-block-18000001.json
opcode-trace-all-20251204143045.json
```

### JSON Structure

**Retrospective Mode / Cumulative File**:

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

**Per-Block File** (RealTime mode):

```json
{
  "metadata": {
    "blockNumber": 18000000,
    "parentHash": "0x1234...",
    "timestamp": 1701698445,
    "transactionCount": 150,
    "tracedAt": "2025-12-04T14:30:45.123Z"
  },
  "opcodeCounts": {
    "ADD": 1523,
    "MUL": 892,
    "SLOAD": 456,
    "SSTORE": 321
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

- StartBlock must be ≤ EndBlock
- Warns when both explicit range and Blocks parameter specified
- At least one range specification required
- EndBlock must not exceed current chain tip
- Output directory must be writable

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

- **Requirement**: Node must be fully synced (SyncMode = WaitingForBlock)
- **Overhead**: <5% impact on block processing
- **Best for**: Continuous monitoring, new block analysis on synced nodes
- **Processing**: ~10 minutes for 1000 blocks (varies by network)
- **During sync**: No blocks will be traced (blocks are downloaded, not executed)

### Retrospective Mode

- **Parallel Processing**: Enabled by setting `MaxDegreeOfParallelism` to a value greater than 0
- **Requirement**: Blocks must exist in the database
- **Overhead**: No impact on live sync
- **Best for**: Historical static analysis of transaction bytecode
- **Processing**: Fast direct database access
- **Limitation**: Counts opcodes in transaction data, not actual EVM execution

## Progress Tracking

Progress is logged every 1000 blocks:
```
[Info] Opcode tracing progress: block 17500000 (50.00% complete)
[Info] Real-time tracing progress: block 18500500 (50.00% complete)
```

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

### RealTime Mode Shows Zero Blocks Processed

This is expected if the node is still syncing. You'll see a warning in the logs:
```
RealTime opcode tracing is enabled, but the node is currently syncing (SyncMode=...).
RealTime mode only captures opcodes from NEW blocks processed at the chain tip AFTER sync completes.
```

**Solutions**:

1. **Wait for sync to complete** - RealTime mode will automatically start tracing once sync finishes
2. **Use Retrospective mode** - For immediate testing, switch to Retrospective mode which analyzes blocks already in the database:
   ```bash
   --OpcodeTracing.Mode Retrospective --OpcodeTracing.StartBlock 100 --OpcodeTracing.EndBlock 200
   ```

### RealTime Mode Produces Empty opcodeCounts

- The node is still syncing (see above)
- The specified block range (StartBlock/EndBlock) hasn't been reached yet
- The node is disconnected from the network

## Additional Resources

- [Nethermind Plugin Documentation](https://docs.nethermind.io/developers/plugins/)

## License

LGPL-3.0-only - See SPDX headers in source files
