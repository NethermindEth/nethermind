// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Utilities;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

/// <summary>
/// Handles real-time opcode tracing by attaching to live block processing.
/// </summary>
public sealed class RealTimeTracer
{
    private readonly OpcodeCounter _counter;
    private readonly BlockRange _range;
    private readonly ILogger _logger;
    private readonly Action<long> _onBlockCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="RealTimeTracer"/> class.
    /// </summary>
    /// <param name="counter">The opcode counter to accumulate into.</param>
    /// <param name="range">The block range to trace.</param>
    /// <param name="onBlockCompleted">Callback invoked when a block in range is completed.</param>
    /// <param name="logManager">The log manager.</param>
    public RealTimeTracer(OpcodeCounter counter, BlockRange range, Action<long> onBlockCompleted, ILogManager logManager)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _range = range;
        _onBlockCompleted = onBlockCompleted ?? throw new ArgumentNullException(nameof(onBlockCompleted));
        _logger = logManager?.GetClassLogger<RealTimeTracer>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    /// <summary>
    /// Handles a completed block trace.
    /// </summary>
    /// <param name="trace">The block trace data.</param>
    internal void OnBlockCompleted(OpcodeBlockTrace trace)
    {
        if (trace is null || !_range.Contains(trace.BlockNumber))
        {
            return;
        }

        // Accumulate opcodes into global counter
        long[] blockCounts = new long[256];
        foreach (var (opcodeName, count) in trace.Opcodes)
        {
            // Map back from name to byte value
            byte opcodeValue = GetOpcodeByteFromName(opcodeName);
            blockCounts[opcodeValue] = count;
        }

        _counter.AccumulateFrom(blockCounts);
        _onBlockCompleted(trace.BlockNumber);
    }

    /// <summary>
    /// Gets the block range being traced.
    /// </summary>
    public BlockRange Range => _range;

    private static byte GetOpcodeByteFromName(string opcodeName)
    {
        // Handle hex format like "0xfe"
        if (opcodeName.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToByte(opcodeName, 16);
        }

        // Try to parse as Instruction enum
        if (Enum.TryParse<Nethermind.Evm.Instruction>(opcodeName, out var instruction))
        {
            return (byte)instruction;
        }

        return 0; // Unknown opcode
    }
}
