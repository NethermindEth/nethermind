// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

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

        /// <summary>Gets the recovered receipt of one transaction without decoding the other receipts or transactions of its block.</summary>
        /// <param name="block">The block containing the transaction. Its memory stays owned by the caller.</param>
        /// <param name="txHash">The hash of the transaction.</param>
        /// <param name="receipt">The receipt, recovered as <see cref="Get(Block, bool, bool)"/> recovers it.</param>
        /// <param name="txIndex">The index of the transaction in <paramref name="block"/>.</param>
        /// <param name="logIndexStart">The number of logs emitted by the transactions preceding it in the block.</param>
        /// <returns><c>false</c> when the finder cannot serve a single receipt, so the caller has to use <see cref="Get(Block, bool, bool)"/>.</returns>
        bool TryGetReceipt(ReceiptRecoveryBlock block, Hash256 txHash, [NotNullWhen(true)] out TxReceipt? receipt, out int txIndex, out int logIndexStart)
        {
            receipt = null;
            txIndex = -1;
            logIndexStart = 0;
            return false;
        }
    }
}
