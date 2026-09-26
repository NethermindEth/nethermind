// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Data;
using Nethermind.Evm;

namespace Nethermind.JsonRpc.Modules
{
    public static class ReceiptFinderExtensions
    {
        public static SearchResult<Hash256> SearchForReceiptBlockHash(this IReceiptFinder receiptFinder, Hash256 txHash)
        {
            Hash256 blockHash = receiptFinder.FindBlockHash(txHash);
            return blockHash is null
                ? new SearchResult<Hash256>($"{txHash} receipt could not be found", ErrorCodes.ResourceNotFound)
                : new SearchResult<Hash256>(blockHash);
        }

        public static ResultWrapper<IEnumerable<ReceiptForRpc>?> GetBlockReceipts(this IReceiptFinder receiptFinder, BlockParameter blockParameter, IBlockFinder blockFinder, ISpecProvider specProvider)
        {
            SearchResult<Block> searchResult = blockFinder.SearchForBlock(blockParameter);
            return searchResult.IsError
                ? ResultWrapper<IEnumerable<ReceiptForRpc>?>.Success(null)
                : receiptFinder.GetBlockReceipts(searchResult.Object, specProvider);
        }

        /// <remarks>The result owns pooled receipts; the RPC stack disposes it after serialization.</remarks>
        internal static ResultWrapper<IEnumerable<ReceiptForRpc>?> GetBlockReceipts(this IReceiptFinder receiptFinder, Block block, ISpecProvider specProvider)
        {
            Transaction[] transactions = block.Transactions;
            TxReceipt[] receipts = receiptFinder.Get(block) ?? new TxReceipt[transactions.Length];
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            int count = Math.Min(receipts.Length, transactions.Length);
            ReceiptsForRpc<ReceiptForRpc> result = new(count);
            try
            {
                // A running sum equals the per-receipt scan only when every index is its position.
                bool positionalIndexes = HasPositionalIndexes(receipts);
                int logIndexStart = 0;
                for (int i = 0; i < count; i++)
                {
                    TxReceipt receipt = receipts[i];
                    Transaction transaction = transactions[i];
                    int receiptLogIndexStart = positionalIndexes ? logIndexStart : receipts.GetBlockLogFirstIndex(receipt.Index);
                    result.Add(new ReceiptForRpc(transaction.Hash, receipt, block.Timestamp, transaction.GetGasInfo(spec, block.Header), receiptLogIndexStart));
                    logIndexStart += receipt.Logs?.Length ?? 0;
                }
            }
            catch
            {
                result.Dispose();
                throw;
            }

            return ResultWrapper<IEnumerable<ReceiptForRpc>?>.Success(result);
        }

        private static bool HasPositionalIndexes(TxReceipt[] receipts)
        {
            for (int i = 0; i < receipts.Length; i++)
            {
                if (receipts[i].Index != i) return false;
            }

            return true;
        }
    }
}
