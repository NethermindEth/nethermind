// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptFinder
    {
        Hash256? FindBlockHash(Hash256 txHash);
        TxReceipt[] Get(Block block);
        TxReceipt[] Get(Hash256 blockHash);
        bool CanGetReceiptsByHash(long blockNumber);
        bool TryGetReceiptsIterator(long blockNumber, Hash256 blockHash, out ReceiptsIterator iterator);
    }
}
