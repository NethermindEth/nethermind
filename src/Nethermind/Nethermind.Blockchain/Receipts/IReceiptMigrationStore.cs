// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptMigrationStore : IReceiptStorage
    {
        void InsertForMigration(Block block, TxReceipt[] receipts);

        /// <summary>Returns the raw stored receipt blob for a block, flushing any deferred write first.</summary>
        /// <remarks>Reads that inspect the stored encoding must go through this rather than the column directly,
        /// so a deferred (buffered) write is observed instead of read as missing.</remarks>
        byte[]? GetReceiptRawData(ulong blockNumber, Hash256 blockHash);
    }
}
