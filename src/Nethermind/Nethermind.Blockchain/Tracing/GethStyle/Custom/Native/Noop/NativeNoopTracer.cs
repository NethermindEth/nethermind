// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;

/// <summary>
/// Runs the transaction with the instruction and action hooks switched off, keeping only receipt tracing, and
/// reports an empty object.
/// </summary>
/// <remarks>
/// The baseline for the cost of traversing a block through the debug API.
/// </remarks>
public sealed class NativeNoopTracer : GethLikeNativeTxTracer
{
    public const string NoopTracer = "noopTracer";

    private readonly Transaction _transaction;

    public NativeNoopTracer(Transaction transaction, GethTraceOptions options) : base(options)
    {
        _transaction = transaction;
        IsTracingActions = false;
        IsTracingMemory = false;
        IsTracingStack = false;
        IsTracingOpLevelStorage = false;
        IsTracingReturnData = false;
    }

    public override bool IsTracingInstructions => false;

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();
        result.TxHash = _transaction.Hash;
        result.CustomTracerResult = new GethLikeCustomTrace();
        return result;
    }
}
