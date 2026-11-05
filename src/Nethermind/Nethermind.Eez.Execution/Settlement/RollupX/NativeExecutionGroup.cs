// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>A proposed cursor becomes current only with its transaction's committed state.</summary>
internal readonly record struct NativeExecutionGroup(bool AwaitingUser)
{
    internal NativeExecutionGroup After(Transaction transaction, out bool completes)
    {
        completes = false;
        if (!transaction.IsEezSystemTransaction())
        {
            completes = AwaitingUser;
            return default;
        }
        if (AwaitingUser) throw NativeL1Header.Invalid("native load must be followed immediately by its user transaction");
        if ((byte)transaction.Type == NativeHistoryEnvelope.Type) return this;
        if (transaction.Type != EezConstants.NativeSystemTxType || transaction.To != EezConstants.RollupXEezl2Address || transaction.Data.Length < 4)
            throw NativeL1Header.Invalid("unsupported native group destination or selector");
        switch (BinaryPrimitives.ReadUInt32BigEndian(transaction.Data.Span))
        {
            case EezCalldata.LoadExecutionTableSelector: return new NativeExecutionGroup(true);
            case EezCalldata.ExecuteIncomingCrossChainCallSelector: completes = true; return default;
            default: throw NativeL1Header.Invalid("unsupported native group selector");
        }
    }

    internal void EnsureComplete()
    {
        if (AwaitingUser) throw NativeL1Header.Invalid("block ends inside an outbound load/user group");
    }
}
