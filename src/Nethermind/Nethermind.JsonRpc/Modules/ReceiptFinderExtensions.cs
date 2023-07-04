// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules
{
    public static class ReceiptFinderExtensions
    {
        public static SearchResult<Keccak> SearchForReceiptBlockHash(this IReceiptFinder receiptFinder, Keccak txHash)
        {
            Keccak blockHash = receiptFinder.FindBlockHash(txHash);
            return blockHash is null
                ? new SearchResult<Keccak>($"{txHash} receipt could not be found", ErrorCodes.ResourceNotFound)
                : new SearchResult<Keccak>(blockHash);
        }
    }
}
