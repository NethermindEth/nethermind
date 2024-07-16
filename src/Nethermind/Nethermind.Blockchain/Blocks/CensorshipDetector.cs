// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.TxPool.Collections;

namespace Nethermind.Blockchain.Blocks;

public class CensorshipDetector
{
    private static CensorshipDetector? _instance;

    public static CensorshipDetector Instance()
    {
        _instance ??= new CensorshipDetector();
        return _instance;
    }
    private const int CacheSize = 4;

    private readonly LruCache<Hash256, TxCensorshipInfo>
        _censorshipDetector = new(CacheSize, CacheSize, "censorshipDetector");

    public struct TxCensorshipInfo
    {
        public bool PotentialCensorship { get; set; }
        public ValueHash256 TxHash { get; set; }

        public TxCensorshipInfo(bool potentialCensorship, ValueHash256 txHash)
        {
            PotentialCensorship = potentialCensorship;
            TxHash = txHash;
        }
    }

    public CensorshipDetector() { }

    public void Delete(Hash256 blockHash)
    {
        _censorshipDetector.Delete(blockHash);
    }

    public void Cache(Block block)
    {
        UInt256 maxGasPriceInBlock = 0;

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            maxGasPriceInBlock = UInt256.Max(maxGasPriceInBlock, block.Transactions[i].GasPrice);
        }

        TxGasPriceSortedCollection.TxHashGasPricePair pair = TxGasPriceSortedCollection.Instance().GetFirstPair();

        _censorshipDetector.Set(block.Hash, new TxCensorshipInfo(pair.GasPrice > maxGasPriceInBlock, pair.Hash));
    }

    public bool CensorshipDetected()
    {
        ValueHash256 toCompare = _censorshipDetector.GetValues()[0].TxHash;
        foreach (TxCensorshipInfo suspect in _censorshipDetector.GetValues())
        {
            if (!suspect.PotentialCensorship || suspect.TxHash != toCompare)
            {
                return false;
            }
        }
        return true;
    }

}
