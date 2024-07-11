// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.TxPool.Collections;

namespace Nethermind.Blockchain.Blocks;

public class PotentialCensorshipCache
{
    private static PotentialCensorshipCache? _instance;

    public static PotentialCensorshipCache Instance()
    {
        _instance ??= new PotentialCensorshipCache();
        return _instance;
    }
    private const int CacheSize = 4;

    private readonly LruCache<Hash256, TxCensorshipDetector>
        _potentialCensorshipCache = new(CacheSize, CacheSize, "potentialCensorshipCache");

    public struct TxCensorshipDetector
    {
        public bool PotentialCensorship { get; set; }
        public ValueHash256 TxHash { get; set; }

        public TxCensorshipDetector(bool potentialCensorship, ValueHash256 txHash)
        {
            PotentialCensorship = potentialCensorship;
            TxHash = txHash;
        }
    }

    public PotentialCensorshipCache() { }

    public void Delete(Hash256 blockHash)
    {
        _potentialCensorshipCache.Delete(blockHash);
    }

    public void Cache(ref Block block)
    {
        UInt256 maxGasPriceInBlock = 0;

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            maxGasPriceInBlock = maxGasPriceInBlock > block.Transactions[i].GasPrice ? maxGasPriceInBlock : block.Transactions[i].GasPrice;
        }

        TxGasPriceSortedCollection.TxHashGasPricePair pair = TxGasPriceSortedCollection.Instance().GetFirstPair();

        if (pair.GasPrice > maxGasPriceInBlock)
        {
            _potentialCensorshipCache.Set(block.Hash, new TxCensorshipDetector(true, pair.Hash));
        }
        else
        {
            _potentialCensorshipCache.Set(block.Hash, new TxCensorshipDetector(false, pair.Hash));
        }
    }

    public bool CensorshipDetected()
    {
        ValueHash256 toCompare = _potentialCensorshipCache.GetValues()[0].TxHash;
        foreach (TxCensorshipDetector suspect in _potentialCensorshipCache.GetValues())
        {
            if (!suspect.PotentialCensorship || suspect.TxHash != toCompare)
            {
                return false;
            }
        }
        return true;
    }

}
