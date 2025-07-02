// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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
            if (searchResult.IsError)
            {
                return ResultWrapper<ReceiptForRpc[]?>.Success(null);
            }

            Block block = searchResult.Object;
            TxReceipt[] receipts = receiptFinder.Get(block) ?? new TxReceipt[block.Transactions.Length];
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            IEnumerable<ReceiptForRpc> result = receipts
                .Zip(block.Transactions, (r, t) =>
                {
                    return new ReceiptForRpc(t.Hash, r, t.GetGasInfo(spec, block.Header), receipts.GetBlockLogFirstIndex(r.Index));
                });
            ReceiptForRpc[] resultAsArray = result.ToArray();
            return ResultWrapper<ReceiptForRpc[]?>.Success(resultAsArray);
        }
    }
}
