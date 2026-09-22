// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Preimage;

/// <summary>Collects KECCAK256 preimages keyed by their hash, including hashes from reverted calls.</summary>
public sealed class NativeKeccakPreimageTracer : GethLikeNativeTxTracer, IInstructionTracingFilter
{
    /// <summary>The Geth tracer name accepted by debug tracing methods.</summary>
    public const string KeccakPreimageTracer = "keccak256PreimageTracer";

    private readonly Transaction _transaction;
    private readonly Dictionary<Hash256, byte[]> _preimages = [];
    private TraceMemory _memory;
    private bool _isKeccak;

    /// <summary>Creates a collector for one transaction execution.</summary>
    public NativeKeccakPreimageTracer(Transaction transaction, GethTraceOptions options) : base(options)
    {
        _transaction = transaction;
        IsTracingMemory = true;
        IsTracingStack = true;
        IsTracingOpLevelStorage = false;
        IsTracingReturnData = false;
    }

    /// <inheritdoc/>
    public UInt256 InstructionMask => UInt256.One << (int)Instruction.KECCAK256;

    /// <inheritdoc/>
    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env) =>
        _isKeccak = opcode == Instruction.KECCAK256;

    /// <inheritdoc/>
    public override void SetOperationMemory(TraceMemory memoryTrace) => _memory = memoryTrace;

    /// <inheritdoc/>
    public override void SetOperationStack(TraceStack stack)
    {
        if (!_isKeccak || stack.Count < 2) return;

        ulong offset = stack.PeekUInt256(0).u0;
        ulong length = stack.PeekUInt256(1).u0;
        if (offset > int.MaxValue || length > int.MaxValue) return;

        ulong end = offset + length;
        // Geth captures before execution, but caps zero padding for invalid or out-of-gas memory ranges.
        if (end > int.MaxValue || (end > _memory.Size && end - _memory.Size > MemorySizes.MiB)) return;

        byte[] preimage = _memory.Slice((int)offset, (int)length).ToArray();
        _preimages[Keccak.Compute(preimage)] = preimage;
    }

    /// <inheritdoc/>
    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();
        result.TxHash = _transaction.Hash;
        result.CustomTracerResult = new GethLikeCustomTrace { Value = _preimages };
        return result;
    }
}
