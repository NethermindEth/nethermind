// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Http.HttpResults;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Xdc.XdcExtensions;

namespace Nethermind.Xdc;

internal class PenaltyHandler(IBlockTree tree, ISpecProvider specProvider, IEpochSwitchManager epochSwitchManager) : IPenaltyHandler
{
    private LruCache<Hash256, Transaction[]> _signTransactionCache = new(XdcConstants.BlockSignersCacheLimit, "XDC Signing Txs Cache");

    private Address[] GetPreviousPenalties(Hash256 currentHash, IXdcReleaseSpec spec, ulong limit)
    {
        var currentEpochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHash);
        if (currentEpochSwitchInfo is null) {
            return null;
        }

        if (limit == 0) return currentEpochSwitchInfo.Penalties;

        var epochNumber = (ulong)spec.SwitchEpoch + currentEpochSwitchInfo.EpochSwitchBlockInfo.Round / (ulong)spec.EpochLength;
        if (epochNumber < limit)
        {
            return null;
        }

        var results = epochSwitchManager.GetBlockByEpochNumber(epochNumber - limit);
        if (results is null) return null;

        var header = (XdcBlockHeader)tree.FindHeader(results.Hash);
        if(header  is null) return null;

        return header.PenaltiesAddress.Value.ToArray();
    }
    public Address[] HandlePenalties(long number, Hash256 currentHash, Address[] candidates)
    {
        DateTime startTime = DateTime.UtcNow;

        List<Hash256> listBlockHash = [currentHash];

        Dictionary<Address, int> minerStatistics = new();

        long parentNumber = number - 1;
        Hash256 parentHash = currentHash;

        ulong round = 0;

        for (int timeout = 0; ; timeout++)
        {
            var parentHeader = (XdcBlockHeader)tree.FindHeader(parentHash);
            if(parentHeader is not null)
            {
                var spec = specProvider.GetXdcSpec(parentHeader);
                round = parentHeader.GetRoundNumber(spec);
                break;
            }

            Task.Delay(1 * 1000).Wait();

            if(timeout > 30)
            {
                return [];
            }
        }

        for (int i = 1; ; i++)
        {
            var parentHeader = (XdcBlockHeader)tree.FindHeader(parentHash);
            var isEpochSwitch = epochSwitchManager.IsEpochSwitchAtBlock(parentHeader);

            if (isEpochSwitch)
            {
                break;
            }

            Address miner = parentHeader.Author;

            if (minerStatistics.ContainsKey(miner)) {
                minerStatistics[miner] = 1;
            } else
            {
                minerStatistics[miner] += 1;
            }

            parentNumber--;
            parentHash = parentHeader.ParentHash;

            listBlockHash.Add(parentHash);
        }

        var currentHeader = (XdcBlockHeader)tree.FindHeader(number);
        var currentSpec = specProvider.GetXdcSpec(currentHeader, round);

        var preMasternodes = epochSwitchManager.GetEpochSwitchInfo(currentHash).Masternodes;

        List<Address> penalties = [];

        int minMinerBlockPerEpoch = XdcConstants.MinimunMinerBlockPerEpoch;
        if(currentSpec.TIP2019Block == number)
        {
            minMinerBlockPerEpoch = currentSpec.MinimumMinerBlockPerEpoch;
        }

        foreach (var minerStat in minerStatistics)
        {
            if(minerStat.Value < minMinerBlockPerEpoch)
            {
                penalties.Add(minerStat.Key);
            }
        }

        foreach (var address in preMasternodes)
        {
            if (!minerStatistics.ContainsKey(address))
            {
                penalties.Add(address);
            }
        }

        if(currentSpec.TIP2019Block == number)
        {
            var comebackHeight = (XdcConstants.LimitPenaltyEpochV2 + 1) * currentSpec.EpochLength + currentSpec.SwitchBlock;
            var penComebacks = new List<Address>();

            if (number > comebackHeight)
            {
                var prevPenalies = GetPreviousPenalties(currentHash, currentSpec, XdcConstants.LimitPenaltyEpochV2);
                penComebacks = prevPenalies.Intersect(candidates).ToList();

                var mapBlockHash = new Dictionary<Hash256, bool>();
                var startRange = XdcConstants.RangeReturnSigner - 1;

                if (startRange >= listBlockHash.Count)
                {
                    startRange = listBlockHash.Count - 1;
                }

                for (int i = startRange; i >= 0; i--)
                {
                    if(penComebacks.Count == 0)
                    {
                        break;
                    }

                    var blockNumber = number - i - 1;
                    var blockHash = listBlockHash[i];

                    if(blockNumber % currentSpec.MergeSignRange == 0)
                    {
                        mapBlockHash[blockHash] = true;
                    }

                    Transaction[] signingTxs = _signTransactionCache.Get(blockHash); // get them from cache
                    if(signingTxs is null)
                    {
                        var blockBody = tree.FindBlock(blockNumber);
                        var transactions = blockBody.Transactions;
                        signingTxs = CacheSigningTransactions(blockHash, transactions, currentSpec); // caches them 
                    }

                    foreach (var tx in signingTxs)
                    {
                        var signedBlockHash = new Hash256(tx.Data.Span[^32..]);
                        var fromSigner = tx.SenderAddress;

                        if(mapBlockHash.ContainsKey(signedBlockHash))
                        {
                            penComebacks.Remove(fromSigner);
                        }
                    }
                }

                foreach (var comeback in penComebacks) 
                {
                    bool ok = true;

                    ok = penalties.Contains(comeback);
                    if (!ok) break;

                    penalties.Add(comeback);
                }
            }
        } else
        {
            var comebackHeight = (currentSpec.LimitPenaltyEpoch + 1) * currentSpec.EpochLength + currentSpec.SwitchBlock;
            if (number > comebackHeight)
            {
                Dictionary<Address, int> penaltiesParole = new();

                Address[] lastPenalty = [];

                for(int i = 0; i < currentSpec.LimitPenaltyEpoch; i++)
                {
                    var previousPenalties = GetPreviousPenalties(currentHash, currentSpec, (ulong)i);
                    foreach (var previousPenalty in previousPenalties)
                    {
                        penaltiesParole[previousPenalty]++;
                    }

                    if(i == 0)
                    {
                        lastPenalty = previousPenalties;
                    }
                }

                var mapBlockHash = new Dictionary<Hash256, bool>();
                var txSignerMap = new Dictionary<Address, int>();
                var startRange = XdcConstants.RangeReturnSigner - 1;

                if(startRange >= listBlockHash.Count)
                {
                    startRange = listBlockHash.Count - 1;
                }

                for(int i = startRange; i >= 0; i--)
                {
                    var blockNumber = number - i - 1;
                    var blockHash = listBlockHash[i];

                    if(blockNumber % currentSpec.MergeSignRange == 0)
                    {
                        mapBlockHash[blockHash] = true;
                    }

                    Transaction[] signingTxs = _signTransactionCache.Get(blockHash); // get them from cache
                    if (signingTxs is null)
                    {
                        var blockBody = tree.FindBlock(blockNumber);
                        var transactions = blockBody.Transactions;
                        signingTxs = CacheSigningTransactions(blockHash, transactions, currentSpec); // caches them 
                    }

                    foreach (var tx in signingTxs)
                    {
                        var signedBlockHash = new Hash256(tx.Data.Span[^32..]);
                        var fromSigner = tx.SenderAddress;

                        if (mapBlockHash.ContainsKey(signedBlockHash))
                        {
                            txSignerMap[fromSigner]++;
                        }
                    }
                }

                foreach (var penalty in lastPenalty)
                {
                    if (penaltiesParole[penalty] == currentSpec.LimitPenaltyEpoch + 1)
                    {
                        if (txSignerMap[penalty] >= currentSpec.MinimumSigningTx)
                        {
                            continue;
                        }
                    }

                    penalties.Add(penalty);
                }
            }
        }

        return penalties.ToArray();
    }

    private Transaction[] CacheSigningTransactions(Hash256 blockHash, Transaction[] transactions, IXdcReleaseSpec spec)
    {
        List<Transaction> signingTxs = [];
        foreach (var tx in transactions)
        {
            if(tx.IsSigningTransaction(spec))
            {
                signingTxs.Add(tx);
            }
        }

        var signingTxArr = signingTxs.ToArray();

        _signTransactionCache.Set(blockHash, signingTxArr);
        return signingTxArr;
    }
}
