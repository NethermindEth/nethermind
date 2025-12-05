// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.OpcodeTracing.Plugin.Output;

namespace Nethermind.OpcodeTracing.Plugin.Tracing;

internal sealed class OpcodeBlockTracer : IBlockTracer
{
    private readonly Action<OpcodeBlockTrace> _onBlockCompleted;
    private OpcodeTraceBuilder? _builder;
    private OpcodeCountingTxTracer? _currentTxTracer;

    public OpcodeBlockTracer(Action<OpcodeBlockTrace> onBlockCompleted)
    {
        _onBlockCompleted = onBlockCompleted ?? throw new ArgumentNullException(nameof(onBlockCompleted));
    }

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        // Rewards do not execute opcodes, so nothing to capture here.
    }

    public void StartNewBlockTrace(Block block)
    {
        _builder = new OpcodeTraceBuilder(block ?? throw new ArgumentNullException(nameof(block)));
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (_builder is null || tx is null)
        {
            _currentTxTracer = null;
            return NullTxTracer.Instance;
        }

        OpcodeCountingTxTracer tracer = new();
        _currentTxTracer = tracer;
        return tracer;
    }

    public void EndTxTrace()
    {
        if (_builder is null || _currentTxTracer is null)
        {
            _currentTxTracer = null;
            return;
        }

        _builder.Accumulate(_currentTxTracer);
        _currentTxTracer = null;
    }

    public void EndBlockTrace()
    {
        if (_builder is null)
        {
            return;
        }

        OpcodeBlockTrace trace = _builder.Build();
        _builder = null;
        _onBlockCompleted(trace);
    }
}

internal sealed record OpcodeBlockTrace
{
    public required string BlockHash { get; init; }
    public required string ParentHash { get; init; }
    public required long BlockNumber { get; init; }
    public required ulong Timestamp { get; init; }
    public required int TransactionCount { get; init; }
    public required IReadOnlyDictionary<string, long> Opcodes { get; init; }
}

internal sealed class OpcodeTraceBuilder
{
    private readonly Block _block;
    private readonly long[] _opcodeCounters = new long[256];
    private int _transactions;

    public OpcodeTraceBuilder(Block block)
    {
        _block = block;
    }

    public void Accumulate(OpcodeCountingTxTracer tracer)
    {
        _transactions++;
        tracer.AccumulateInto(_opcodeCounters);
    }

    public OpcodeBlockTrace Build()
    {
        Dictionary<string, long> opcodeMap = new();
        for (int opcode = 0; opcode < _opcodeCounters.Length; opcode++)
        {
            long count = _opcodeCounters[opcode];
            if (count == 0)
            {
                continue;
            }

            opcodeMap[OpcodeLabelCache.GetLabel((byte)opcode)] = count;
        }

        Hash256 blockHash = _block.Hash ?? _block.Header.Hash ?? Keccak.Zero;
        Hash256 parentHash = _block.ParentHash ?? Keccak.Zero;

        return new OpcodeBlockTrace
        {
            BlockHash = blockHash.ToString(),
            ParentHash = parentHash.ToString(),
            BlockNumber = _block.Number,
            Timestamp = _block.Timestamp,
            TransactionCount = _transactions,
            Opcodes = opcodeMap
        };
    }
}

