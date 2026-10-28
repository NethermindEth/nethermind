// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// https://github.com/ethereum/EIPs
    /// </summary>
    public interface IReceiptSpec
    {

        /// <summary>
        /// Byzantium Embedding transaction return data in receipts
        /// </summary>
        bool IsEip658Enabled { get; }

        /// <summary>
        /// EIP-7778: Block Gas Accounting without Refunds
        /// </summary>
        bool IsEip7778Enabled { get; }

        /// <summary>
        /// EIP-8116: Replace cumulative receipt fields
        /// </summary>
        /// <remarks>
        /// The receipt's <c>cumulativeGasUsed</c> field holds the transaction's own gas used, and JSON-RPC
        /// <c>logIndex</c> counts within the receipt rather than the block.
        /// </remarks>
        bool IsEip8116Enabled { get; }

        /// <summary>
        /// EIP-7668: Remove bloom filters
        /// </summary>
        /// <remarks>
        /// The logs bloom of the header and of every receipt is a zero-length byte string (RLP <c>0x80</c>)
        /// instead of the 256-byte filter; see <see cref="Bloom.ZeroLength"/>.
        /// </remarks>
        bool IsEip7668Enabled { get; }

        /// <summary>
        /// Should validate ReceiptsRoot.
        /// </summary>
        /// <remarks>Backward compatibility for early Kovan blocks.</remarks>
        bool ValidateReceipts => true;

    }
}
