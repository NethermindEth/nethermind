// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using System;
using System.Linq;

namespace Nethermind.Xdc;

public class SigningTxCache : ISigningTxCache
{
    private const BlockTreeLookupOptions SigningTxLookupOptions =
        BlockTreeLookupOptions.TotalDifficultyNotNeeded;

    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly LruCache<Hash256, Transaction[]> _signingTxsCache = new(XdcConstants.BlockSignersCacheLimit, "XDC Signing Txs Cache");
    private readonly LruCache<Hash256, XdcBlockHeader> _headersCache = new(XdcConstants.BlockSignersCacheLimit, "XDC Signing Headers Cache");

    public SigningTxCache(IBlockTree blockTree, ISpecProvider specProvider)
    {
        _blockTree = blockTree;
        _specProvider = specProvider;
        _blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    public Transaction[] GetSigningTransactions(Hash256 blockHash, long blockNumber, IXdcReleaseSpec spec)
    {
        if (_signingTxsCache.TryGet(blockHash, out Transaction[] signingTxs))
            return signingTxs;

        Block block = _blockTree.FindBlock(blockHash, SigningTxLookupOptions, blockNumber)
            ?? throw new InvalidOperationException($"Expected block {blockHash} at number {blockNumber} to exist in block tree.");
        return CacheSigningTransactions(blockHash, block, spec);
    }

    public void SetSigningTransactions(Hash256 blockHash, Transaction[] transactions) => _signingTxsCache.Set(blockHash, transactions);

    public bool TryGetHeader(Hash256 blockHash, out XdcBlockHeader? header) => _headersCache.TryGet(blockHash, out header);

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        => CacheSigningTransactions(e.Block);

    public void CacheSigningTransactions(Block block)
    {
        if (block.Header is not XdcBlockHeader xdcHeader)
            return;

        Hash256 calculatedHash = xdcHeader.CalculateHash().ToHash256();
        Hash256 blockHash = block.Hash ?? calculatedHash;
        _headersCache.Set(blockHash, xdcHeader);
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader);
        _ = CacheSigningTransactions(blockHash, block, spec!);
    }

    private Transaction[] CacheSigningTransactions(Hash256 blockHash, Block block, IXdcReleaseSpec spec)
    {
        Transaction[] cached = block.Transactions.Where(tx => tx.IsSigningTransaction(spec)).ToArray();
        _signingTxsCache.Set(blockHash, cached);
        return cached;
    }
}
