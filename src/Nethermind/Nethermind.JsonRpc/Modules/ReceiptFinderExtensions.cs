// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules
{
    public static class ReceiptFinderExtensions
    {
        public static SearchResult<Commitment> SearchForReceiptBlockHash(this IReceiptFinder receiptFinder, Commitment txHash)
        {
            Commitment blockHash = receiptFinder.FindBlockHash(txHash);
            return blockHash is null
                ? new SearchResult<Commitment>($"{txHash} receipt could not be found", ErrorCodes.ResourceNotFound)
                : new SearchResult<Commitment>(blockHash);
        }
    }
}
