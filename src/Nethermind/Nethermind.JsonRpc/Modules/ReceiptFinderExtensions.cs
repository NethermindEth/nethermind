// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Data;
using Nethermind.Evm;
using Nethermind.Int256;

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

        internal static ResultWrapper<IEnumerable<ReceiptForRpc>?> GetBlockReceipts(this IReceiptFinder receiptFinder, Block block, ISpecProvider specProvider)
        {
            Transaction[] transactions = block.Transactions;
            TxReceipt[] receipts = receiptFinder.Get(block) ?? new TxReceipt[transactions.Length];
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            UInt256? feePerBlobGas = null;
            // EIP-8116: logIndex counts within the receipt.
            ReceiptsForRpc<ReceiptForRpc> result = new(receipts, transactions, spec.IsEip8116Enabled, (target, receipt, transaction, logIndexStart) =>
                target.Populate(transaction.Hash, receipt, block.Timestamp, transaction.GetGasInfo(spec, block.Header, ref feePerBlobGas), logIndexStart));

            return ResultWrapper<IEnumerable<ReceiptForRpc>?>.Success(result);
        }
    }
}
