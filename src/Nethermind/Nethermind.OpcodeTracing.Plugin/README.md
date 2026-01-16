# Nethermind.OpcodeTracing.Plugin

The Opcode Tracing Plugin enables tracing of EVM opcode usage across configurable block ranges. It supports both real-time tracing during block processing and retrospective analysis of historical blocks.

## Features

- **Three Tracing Modes**:
  - **RealTime**: Traces opcodes as blocks are processed at the chain tip AFTER sync completes
  - **Retrospective**: Analyzes transaction bytecode from historical blocks in the database (static analysis)
  - **RetrospectiveExecution**: Replays historical transactions through the EVM to capture actual executed opcodes including internal calls

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

**Note**: This mode counts opcodes found in transaction data bytes, which may be different from actual executed opcodes. For true execution tracing of historical blocks, use **RetrospectiveExecution** mode instead.

### RetrospectiveExecution Mode

RetrospectiveExecution mode replays historical transactions through the actual EVM to capture all executed opcodes, including those from internal contract calls (CALL, DELEGATECALL, STATICCALL, etc.). Unlike Retrospective mode which only analyzes bytecode statically, this mode provides accurate execution-based opcode counts.

**How it works**:

1. For each block in the specified range, creates an isolated read-only state based on the parent block
2. Replays each transaction through the EVM with instruction-level tracing enabled
3. Captures every opcode executed, including opcodes from internal calls, CREATE operations, and precompile interactions
4. Accumulates counts into the final output without modifying any chain state

**Requirements**:

- **Archive node required**: Historical state must be available for the blocks being traced
- **State availability**: Parent block state must exist; blocks with pruned state are skipped with warnings

**Not suitable for**:

- Nodes running with state pruning (use archive mode: `--Pruning.Mode None`)
- Very old blocks where state has been pruned

**Parallel Processing**:

RetrospectiveExecution mode supports parallel block processing via `MaxDegreeOfParallelism`. Each block is processed in an isolated state scope, making it safe to execute multiple blocks concurrently:

```bash
--OpcodeTracing.Mode RetrospectiveExecution --OpcodeTracing.MaxDegreeOfParallelism 8
```

### Mode Comparison

| Feature | RealTime | Retrospective | RetrospectiveExecution |
|---------|----------|---------------|------------------------|
| **Analysis Type** | Live EVM execution | Static bytecode analysis | Historical EVM replay |
| **Captures Internal Calls** | Yes | No | Yes |
| **Requires Sync Complete** | Yes | No | No |
| **Requires Archive Node** | No | No | Yes |
| **Parallel Processing** | No (per-block) | Yes | Yes |
| **State Requirements** | None | None | Historical state |
| **Accuracy** | Exact (live execution) | Approximate (bytecode only) | Exact (replayed execution) |
| **Best Use Case** | Production monitoring | Quick research | Accurate historical analysis |
| **Output Files** | Per-block + cumulative | Single file | Single file |

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
| `Mode` | string | "RealTime" | Tracing mode: RealTime, Retrospective, or RetrospectiveExecution |
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

**Retrospective Mode**:

```
opcode-trace-{startBlock}-{endBlock}.json
```
Example: `opcode-trace-18000000-18001000.json`

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

### Trace Historical Blocks with EVM Execution

```bash
# Replay historical transactions through the EVM (requires archive node)
dotnet run --project Nethermind.Runner -- \\
  --config mainnet \\
  --Pruning.Mode None \\
  --OpcodeTracing.Enabled true \\
  --OpcodeTracing.StartBlock 17000000 \\
  --OpcodeTracing.EndBlock 17001000 \\
  --OpcodeTracing.Mode RetrospectiveExecution \\
  --OpcodeTracing.MaxDegreeOfParallelism 4 \\
  --OpcodeTracing.OutputDirectory ./traces
```

This mode captures actual executed opcodes including internal calls, providing accurate opcode counts.

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

### RetrospectiveExecution Mode: "State unavailable" Warnings

When using RetrospectiveExecution mode, you may see warnings like:
```
State unavailable for block 12345678, skipping (parent state may be pruned)
```

**Causes**:

1. **Node is not running in archive mode**: RetrospectiveExecution requires historical state to replay transactions. Standard nodes prune old state.
2. **Block is older than pruning window**: Even with some state retention, very old blocks may have their state pruned.
3. **State not yet downloaded**: During sync, historical state may not be available yet.

**Solutions**:

1. **Use an archive node**: Run Nethermind with pruning disabled:
   ```bash
   --Pruning.Mode None
   ```
   Note: Archive nodes require significantly more disk space (several TB for mainnet).

2. **Check the skippedBlocks in output**: The JSON output includes a `skippedBlocks` array listing all blocks that couldn't be traced due to unavailable state.

3. **Use a more recent block range**: If you don't have archive state, trace more recent blocks where state is still available.

4. **Wait for sync to complete**: If syncing, historical state becomes available as sync progresses.

### RetrospectiveExecution Mode: Blocks Being Skipped

If many blocks are being skipped in RetrospectiveExecution mode:

1. **Check skippedBlocks array**: Review the output JSON's `skippedBlocks` field
2. **Verify state availability**: Ensure your node has state for the requested block range
3. **Check logs**: Look for specific error messages about why blocks failed

### RetrospectiveExecution Mode: Execution Tracing is Slow

Execution tracing is slow because it requires replaying each transaction through the EVM. You can optimize it by using the `--OpcodeTracing.MaxDegreeOfParallelism` parameter to trace blocks in parallel.
You may also need to wait for the node to sync to the block range you are tracing. Actual tracing happens after the logs like

```
2026-01-14 12:52:11.0234|INFO|OpcodeTracing.Plugin.OpcodeTracingPlugin|14|Opcode tracing plugin initialized (session=20260114125211).
2026-01-14 12:58:51.1945|INFO|OpcodeTracing.Plugin.Tracing.OpcodeTraceRecorder|17|Starting RetrospectiveExecution tracing of 1980 blocks
2026-01-14 12:58:51.1945|INFO|OpcodeTracing.Plugin.Tracing.RetrospectiveExecutionTracer|17|RetrospectiveExecution tracing with MaxDegreeOfParallelism=4
```

### Is it possible to see only plugin logs?

The easiest way to check tracer progress is to add the following to the `nlog.config` file:
```xml
    <target name="opcode-tracing-file" xsi:type="File"
            fileName="opcode-tracing.txt"
    <logger name="OpcodeTracing.Plugin.*" minlevel="Debug" writeTo="opcode-tracing-file" />
```

And then use `--loggerConfigSource=/nethermind/data/customNLog.config` with the Nethermind CLI. The logs will appear in the `opcode-tracing.txt` file.

## Additional Resources

- [Nethermind Plugin Documentation](https://docs.nethermind.io/developers/plugins/)

