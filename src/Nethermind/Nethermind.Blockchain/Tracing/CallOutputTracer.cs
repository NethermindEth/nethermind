// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Tracing;

public class CallOutputTracer : TxTracer
{
    public override bool IsTracingReceipt => true;
    public override bool IsCollectingLogs => false;
    public byte[]? ReturnValue { get; set; }

    public ulong GasSpent { get; set; }
    public ulong OperationGas { get; set; }

    /// <summary>The peak gas the transaction consumed, before refunds and at least the calldata floor.</summary>
    public ulong MaxUsedGas { get; set; }

    public string? Error { get; set; }

    public byte StatusCode { get; set; }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs,
        Hash256? stateRoot = null)
    {
        GasSpent = gasSpent.SpentGas;
        OperationGas = gasSpent.OperationGas;
        MaxUsedGas = gasSpent.EffectiveMaxUsedGas;
        ReturnValue = output;
        StatusCode = Evm.StatusCode.Success;
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error,
        Hash256? stateRoot = null)
    {
        GasSpent = gasSpent.SpentGas;
        OperationGas = gasSpent.OperationGas;
        MaxUsedGas = gasSpent.EffectiveMaxUsedGas;
        Error = error;
        ReturnValue = output;
        StatusCode = Evm.StatusCode.Failure;
    }

    public void Reset()
    {
        GasSpent = 0;
        OperationGas = 0;
        MaxUsedGas = 0;
        ReturnValue = null;
        Error = null;
        StatusCode = 0;
    }
}
