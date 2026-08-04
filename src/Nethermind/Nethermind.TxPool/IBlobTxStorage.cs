// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.TxPool;

public interface IBlobTxStorage : ITxStorage
{
    bool TryGetBlobTransactionsFromBlock(ulong blockNumber, out Transaction[]? blockBlobTransactions);
    void AddBlobTransactionsFromBlock(ulong blockNumber, in ArrayPoolListRef<Transaction> blockBlobTransactions);
    void DeleteBlobTransactionsFromBlock(ulong blockNumber);
}
