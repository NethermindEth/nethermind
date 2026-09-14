// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;

// noopTracer runs the transaction with every tracing hook switched off and reports an empty object,
// which makes it the baseline for the cost of traversing a block through the debug API.
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

    protected override GethLikeTxTrace CreateTrace() => new();

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();
        result.TxHash = _transaction.Hash;
        result.CustomTracerResult = new GethLikeCustomTrace();
        return result;
    }
}
