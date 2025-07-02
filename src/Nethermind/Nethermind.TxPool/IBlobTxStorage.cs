// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool;

public interface IBlobTxStorage : ITxStorage
{
    bool TryGetBlobTransactionsFromBlock(long blockNumber, out Transaction[]? blockBlobTransactions);
    void AddBlobTransactionsFromBlock(long blockNumber, IList<Transaction> blockBlobTransactions);
    void DeleteBlobTransactionsFromBlock(long blockNumber);
}
