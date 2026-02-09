// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

internal sealed class ParallelFeeRecorder(
    int txIndex,
    FeeAccumulator feeAccumulator,
    MultiVersionMemoryScopeProvider scopeProvider) : IFeeRecorder
{
    private readonly int _txIndex = txIndex;
    private readonly FeeAccumulator _feeAccumulator = feeAccumulator;
    private readonly MultiVersionMemoryScopeProvider _scopeProvider = scopeProvider;

    public void RecordFee(Address recipient, in UInt256 amount, bool createAccount)
    {
        FeeRecipientKind? kind = GetFeeKind(recipient);
        if (kind is null)
        {
            return;
        }

        _feeAccumulator.RecordFee(_txIndex, recipient, in amount, createAccount);

        ParallelStateKey key = ParallelStateKey.ForFee(kind.Value, _txIndex);
        if (_scopeProvider.WriteSet.TryGetValue(key, out object? existing) && existing is UInt256 existingAmount)
        {
            _scopeProvider.WriteSet[key] = existingAmount + amount;
        }
        else
        {
            _scopeProvider.WriteSet[key] = amount;
        }
    }

    private FeeRecipientKind? GetFeeKind(Address recipient)
    {
        if (_feeAccumulator.GasBeneficiary is not null && recipient == _feeAccumulator.GasBeneficiary)
        {
            return FeeRecipientKind.GasBeneficiary;
        }

        if (_feeAccumulator.FeeCollector is not null && recipient == _feeAccumulator.FeeCollector)
        {
            return FeeRecipientKind.FeeCollector;
        }

        return null;
    }
}
