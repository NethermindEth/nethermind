// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptFinder
    {
        Commitment? FindBlockHash(Commitment txHash);
        TxReceipt[] Get(Block block);
        TxReceipt[] Get(Commitment blockHash);
        bool CanGetReceiptsByHash(long blockNumber);
        bool TryGetReceiptsIterator(long blockNumber, Commitment blockHash, out ReceiptsIterator iterator);
    }
}
