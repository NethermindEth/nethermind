// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.TxPool;

public interface IBlobTxStorage : ITxStorage
{
    bool TryGetBlobTransactionsFromBlock(ulong blockNumber, [NotNullWhen(true)] out Transaction[]? blockBlobTransactions);
    void AddBlobTransactionsFromBlock(ulong blockNumber, in ArrayPoolListRef<Transaction> blockBlobTransactions);
    void DeleteBlobTransactionsFromBlock(ulong blockNumber);
}
