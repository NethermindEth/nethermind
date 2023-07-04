// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptFinder
    {
        Keccak? FindBlockHash(Keccak txHash);
        TxReceipt[] Get(Block block);
        TxReceipt[] Get(Keccak blockHash);
        bool CanGetReceiptsByHash(long blockNumber);
        bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator);
    }
}
