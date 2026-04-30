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
    private readonly object _lock = new();
    private readonly AsyncLocal<OpcodeCountingTxTracer?> _currentTxTracer = new();
    private OpcodeTraceBuilder? _builder;

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        // Rewards do not execute opcodes, so nothing to capture here.
    }

    public void StartNewBlockTrace(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);

        lock (_lock)
        {
            _builder = new OpcodeTraceBuilder(block);
        }
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (tx is null)
        {
            _currentTxTracer.Value = null;
            return NullTxTracer.Instance;
        }

        lock (_lock)
        {
            if (_builder is null)
            {
                _currentTxTracer.Value = null;
                return NullTxTracer.Instance;
            }
        }

        OpcodeCountingTxTracer tracer = new();
        _currentTxTracer.Value = tracer;
        return tracer;
    }

    public void EndTxTrace()
    {
        OpcodeCountingTxTracer? currentTxTracer = _currentTxTracer.Value;
        _currentTxTracer.Value = null;
        if (currentTxTracer is null)
        {
            return;
        }

        lock (_lock)
        {
            _builder?.Accumulate(currentTxTracer);
        }
    }

    public void EndBlockTrace()
    {
        OpcodeBlockTrace trace;
        lock (_lock)
        {
            if (_builder is null)
            {
                return;
            }

            trace = _builder.Build();
            _builder = null;
        }

        _onBlockCompleted(trace);
    }
}

internal sealed record OpcodeBlockTrace
{
    public required Hash256 BlockHash { get; init; }
    public required Hash256 ParentHash { get; init; }
    public required long BlockNumber { get; init; }
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
        _transactions++;
        tracer.AccumulateInto(_opcodeCounters);
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
            TransactionCount = _transactions,
            Opcodes = opcodeMap
        };
    }
}
