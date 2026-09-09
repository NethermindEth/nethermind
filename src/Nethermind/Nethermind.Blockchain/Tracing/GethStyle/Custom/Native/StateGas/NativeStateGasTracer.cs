// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;

public sealed class NativeStateGasTracer : GethLikeNativeTxTracer
{
    public const string StateGasTracer = "stateGasTracer";

    private readonly Hash256? _txHash;
    private readonly bool _isEip8037Enabled;
    private StateGasTrace? _result;

    public NativeStateGasTracer(Transaction tx, IReleaseSpec spec, GethTraceOptions options) : base(options)
    {
        _txHash = tx.Hash;
        _isEip8037Enabled = spec.IsEip8037Enabled;
        // Terminal-only: reads only the final GasConsumed, so skip the per-opcode storage/stack callbacks.
        IsTracingOpLevelStorage = false;
        IsTracingStack = false;
    }

    public override bool IsTracingInstructions => false;

    protected override GethLikeTxTrace CreateTrace() => new();

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        Capture(in gasSpent);
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        Capture(in gasSpent);
    }

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();
        result.TxHash = _txHash;
        result.CustomTracerResult = new GethLikeCustomTrace { Value = _result ?? new StateGasTrace() };
        return result;
    }

    private void Capture(in GasConsumed gasSpent) =>
        _result = new StateGasTrace
        {
            GasUsed = gasSpent.SpentGas,
            RegularGasUsed = _isEip8037Enabled ? gasSpent.EffectiveBlockGas : gasSpent.EffectiveMaxUsedGas,
            StateGasUsed = _isEip8037Enabled ? gasSpent.BlockStateGas : 0,
            GasRefund = gasSpent.GasRefund,
        };
}
