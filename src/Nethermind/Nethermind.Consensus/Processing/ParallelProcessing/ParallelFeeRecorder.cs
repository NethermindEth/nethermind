// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

internal sealed class ParallelFeeRecorder(
    int txIndex,
    FeeAccumulator feeAccumulator,
    MultiVersionMemoryScopeProvider scopeProvider) : IFeeRecorder
{
    public void RecordFee(Address recipient, in UInt256 amount, bool createAccount)
    {
        FeeRecipientKind kind = feeAccumulator.GetFeeKind(recipient);
        if (kind != FeeRecipientKind.None)
        {
            feeAccumulator.RecordFee(txIndex, recipient, in amount, createAccount);
            ParallelStateKey key = ParallelStateKey.ForFee(kind, txIndex);
            ref object value = ref CollectionsMarshal.GetValueRefOrAddDefault(scopeProvider.WriteSet, key, out bool exists);
            value = exists && value is UInt256 existingAmount ? existingAmount + amount : amount;
        }
    }
}
