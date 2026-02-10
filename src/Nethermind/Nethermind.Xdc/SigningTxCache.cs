// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Spec;
using System.Linq;

namespace Nethermind.Xdc;

public class SigningTxCache(IBlockTree blockTree) : ISigningTxCache
{
    private readonly LruCache<Hash256, Transaction[]> _signingTxsCache = new(XdcConstants.BlockSignersCacheLimit, "XDC Signing Txs Cache");

    public Transaction[] GetSigningTransactions(Hash256 blockHash, long blockNumber, IXdcReleaseSpec spec)
    {
        if (_signingTxsCache.TryGet(blockHash, out Transaction[] signingTxs))
        {
            return signingTxs;
        }

        Block? block = blockTree.FindBlock(blockHash, blockNumber);
        if (block is null)
        {
            return [];
        }

        Transaction[] cached = block.Transactions.Where(tx => tx.IsSigningTransaction(spec)).ToArray();
        _signingTxsCache.Set(blockHash, cached);
        return cached;
    }
}
