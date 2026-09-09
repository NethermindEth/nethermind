// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptFinder
    {
        /// <summary>
        /// Registration key for the finder that may reproduce absent receipt bodies by re-executing their block.
        /// </summary>
        /// <remarks>
        /// The unkeyed registration always serves stored receipts only, so consensus components and peer-facing
        /// serving cannot drive a block execution. Read-only query paths resolve this key instead. Registered
        /// unconditionally: without <see cref="IReceiptConfig.DeriveFromState"/> it resolves the stored-only finder.
        /// </remarks>
        const string RegenerableKey = "regenerable-receipts";

        Hash256? FindBlockHash(Hash256 txHash);
        TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true);
        TxReceipt[] Get(Hash256 blockHash, bool recover = true);
        bool CanGetReceiptsByHash(ulong blockNumber);
        bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator);
    }
}
