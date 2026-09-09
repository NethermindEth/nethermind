// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

internal sealed class OpcodeBlockTracer(Action<OpcodeBlockTrace> onBlockCompleted) : IParallelSafeBlockTracer
{
    private readonly Action<OpcodeBlockTrace> _onBlockCompleted = onBlockCompleted ?? throw new ArgumentNullException(nameof(onBlockCompleted));
    private OpcodeTraceBuilder? _builder;

    // Transaction execution is synchronous; the standard processing path ends
    // the trace on the same worker thread that started it.
    [ThreadStatic]
    private static Dictionary<OpcodeBlockTracer, OpcodeCountingTxTracer>? _currentTxTracers;

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        // Rewards do not execute opcodes, so nothing to capture here.
    }

    public void StartNewBlockTrace(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);

        Volatile.Write(ref _builder, new OpcodeTraceBuilder(block));
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (tx is null)
        {
            ClearCurrentTxTracer();
            return NullTxTracer.Instance;
        }

        OpcodeTraceBuilder? builder = Volatile.Read(ref _builder);
        if (builder is null)
        {
            ClearCurrentTxTracer();
            return NullTxTracer.Instance;
        }

        OpcodeCountingTxTracer tracer = new(builder);
        (_currentTxTracers ??= [])[this] = tracer;
        return tracer;
    }

    public void EndTxTrace()
    {
        Dictionary<OpcodeBlockTracer, OpcodeCountingTxTracer>? currentTxTracers = _currentTxTracers;
        if (currentTxTracers is null || !currentTxTracers.Remove(this, out OpcodeCountingTxTracer? currentTxTracer))
        {
            return;
        }

        if (currentTxTracers.Count == 0)
        {
            _currentTxTracers = null;
        }

        currentTxTracer?.Dispose();
    }

    public void EndBlockTrace()
    {
        OpcodeTraceBuilder? builder = Interlocked.Exchange(ref _builder, null);
        if (builder is null)
        {
            return;
        }

        _onBlockCompleted(builder.Build());
    }

    private void ClearCurrentTxTracer()
    {
        Dictionary<OpcodeBlockTracer, OpcodeCountingTxTracer>? currentTxTracers = _currentTxTracers;
        if (currentTxTracers is not null && currentTxTracers.Remove(this) && currentTxTracers.Count == 0)
        {
            _currentTxTracers = null;
        }
    }
}

internal sealed record OpcodeBlockTrace
{
    public required Hash256 BlockHash { get; init; }
    public required Hash256 ParentHash { get; init; }
    public required ulong BlockNumber { get; init; }
    public required ulong Timestamp { get; init; }
    public required int TransactionCount { get; init; }
    public required IReadOnlyDictionary<byte, long> Opcodes { get; init; }
}

internal sealed class OpcodeTraceBuilder(Block block)
{
    private readonly Block _block = block;
    private readonly long[] _opcodeCounters = new long[256];
    private int _transactions;

    public void Accumulate(OpcodeCountingTxTracer tracer)
    {
        Interlocked.Increment(ref _transactions);
        tracer.AccumulateIntoThreadSafe(_opcodeCounters);
    }

    public OpcodeBlockTrace Build()
    {
        Dictionary<byte, long> opcodeMap = [];
        for (int opcode = 0; opcode < _opcodeCounters.Length; opcode++)
        {
            long count = _opcodeCounters[opcode];
            if (count == 0)
            {
                continue;
            }

            opcodeMap[(byte)opcode] = count;
        }

        return new OpcodeBlockTrace
        {
            BlockHash = _block.Hash ?? _block.Header.Hash ?? Keccak.Zero,
            ParentHash = _block.ParentHash ?? Keccak.Zero,
            BlockNumber = _block.Number,
            Timestamp = _block.Timestamp,
            TransactionCount = Volatile.Read(ref _transactions),
            Opcodes = opcodeMap
        };
    }
}
