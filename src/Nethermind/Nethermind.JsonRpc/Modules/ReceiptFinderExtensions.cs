// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        public static ResultWrapper<ReceiptForRpc[]?> GetBlockReceipts(this IReceiptFinder receiptFinder, BlockParameter blockParameter, IBlockFinder blockFinder, ISpecProvider specProvider)
        {
            SearchResult<Block> searchResult = blockFinder.SearchForBlock(blockParameter);
            return searchResult.IsError
                ? ResultWrapper<ReceiptForRpc[]?>.Success(null)
                : receiptFinder.GetBlockReceipts(searchResult.Object, specProvider);
        }

        internal static ResultWrapper<ReceiptForRpc[]?> GetBlockReceipts(this IReceiptFinder receiptFinder, Block block, ISpecProvider specProvider)
        {
            Transaction[] transactions = block.Transactions;
            TxReceipt[] receipts = receiptFinder.Get(block) ?? new TxReceipt[transactions.Length];
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            ReceiptForRpc[] result = new ReceiptForRpc[Math.Min(receipts.Length, transactions.Length)];
            // A running sum equals the per-receipt scan only when every index is its position.
            bool positionalIndexes = HasPositionalIndexes(receipts);
            int logIndexStart = 0;
            for (int i = 0; i < result.Length; i++)
            {
                TxReceipt receipt = receipts[i];
                Transaction transaction = transactions[i];
                int receiptLogIndexStart = positionalIndexes ? logIndexStart : receipts.GetBlockLogFirstIndex(receipt.Index);
                result[i] = new ReceiptForRpc(transaction.Hash, receipt, block.Timestamp, transaction.GetGasInfo(spec, block.Header), receiptLogIndexStart);
                logIndexStart += receipt.Logs?.Length ?? 0;
            }

            return ResultWrapper<ReceiptForRpc[]?>.Success(result);
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
