// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>Refund outcome: <see cref="GasConsumed"/> on success, or a failed <see cref="TransactionResult"/> when a refund path detects a violated EIP-8037 state-gas invariant.</summary>
public readonly struct RefundResult
{
    private RefundResult(GasConsumed gas, TransactionResult result)
    {
        Gas = gas;
        Result = result;
    }

    public GasConsumed Gas { get; }
    public TransactionResult Result { get; }

    public static implicit operator RefundResult(GasConsumed gas) => new(gas, TransactionResult.Ok);

    // ulong conversion so the pre-EIP-8037 `return tx.GasLimit;` refund returns compile unchanged.
    public static implicit operator RefundResult(ulong spentGas) => new((GasConsumed)spentGas, TransactionResult.Ok);

    public static RefundResult Invalid(TransactionResult error) => new(default, error);
}
