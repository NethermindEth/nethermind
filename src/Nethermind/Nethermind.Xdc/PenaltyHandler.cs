// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Http.HttpResults;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Xdc.XdcExtensions;

namespace Nethermind.Xdc;

internal class PenaltyHandler(IBlockTree tree, IEthereumEcdsa ethereumEcdsa, ISpecProvider specProvider, IEpochSwitchManager epochSwitchManager) : IPenaltyHandler
{
    private LruCache<Hash256, Transaction[]> _signTransactionCache = new(XdcConstants.BlockSignersCacheLimit, "XDC Signing Txs Cache");

    private Address[] GetPreviousPenalties(Hash256 currentHash, IXdcReleaseSpec spec, ulong limit)
    {
        var currentEpochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHash);
        if (currentEpochSwitchInfo is null)
        {
            return [];
        }

        if (limit == 0) return currentEpochSwitchInfo.Penalties;

        var epochNumber = (ulong)spec.SwitchEpoch + currentEpochSwitchInfo.EpochSwitchBlockInfo.Round / (ulong)spec.EpochLength;
        if (epochNumber < limit)
        {
            return [];
        }

        var results = epochSwitchManager.GetBlockByEpochNumber(epochNumber - limit);
        if (results is null) return [];

        var header = (XdcBlockHeader)tree.FindHeader(results.Hash);
        if(header  is null) return [];

        return [..header.PenaltiesAddress.Value];
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

            var xdcHeaderDecoder = new XdcHeaderDecoder();
            Address miner = parentHeader.Beneficiary ?? ethereumEcdsa.RecoverAddress(new Signature(parentHeader.Validator.AsSpan(0, 64), parentHeader.Validator[64]), Keccak.Compute(xdcHeaderDecoder.Encode(parentHeader, RlpBehaviors.ForSealing).Bytes));

            if (!minerStatistics.ContainsKey(miner)) {
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
        if(currentSpec.TipUpgradePenalty <= number)
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

        bool isTipUpgradePenalty = currentSpec.TipUpgradePenalty <= number;

        if (!isTipUpgradePenalty)
        {
            var comebackHeight = (currentSpec.LimitPenaltyEpochV2 + 1) * (ulong)currentSpec.EpochLength + (ulong)currentSpec.SwitchBlock;
            var penComebacks = new List<Address>();

            if ((ulong)number > comebackHeight)
            {
                var prevPenalies = GetPreviousPenalties(currentHash, currentSpec, currentSpec.LimitPenaltyEpochV2);
                penComebacks = prevPenalies.Intersect(candidates).ToList();

                var mapBlockHash = new Dictionary<Hash256, bool>();
                var startRange = (int)currentSpec.RangeReturnSigner - 1;

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
                    if (!penalties.Contains(comeback))
                    {
                        penalties.Add(comeback);
                    }
                }
            }
        } else
        {
            var comebackHeight = (currentSpec.LimitPenaltyEpoch + 1ul) * (ulong)currentSpec.EpochLength + (ulong)currentSpec.SwitchBlock;
            if ((ulong)number > comebackHeight)
            {
                Dictionary<Address, ulong> penaltiesParole = new();

                Address[] lastPenalty = [];

                for(ulong i = 0; i <= currentSpec.LimitPenaltyEpoch; i++)
                {
                    var previousPenalties = GetPreviousPenalties(currentHash, currentSpec, (ulong)i);
                    foreach (var previousPenalty in previousPenalties)
                    {
                        if (!penaltiesParole.TryGetValue(previousPenalty, out var count))
                        {
                            penaltiesParole[previousPenalty] = 1;
                        }
                        else
                        {
                            penaltiesParole[previousPenalty] = count + 1;
                        }
                    }

                    if (i == 0)
                    {
                        lastPenalty = previousPenalties;
                    }
                }

                var mapBlockHash = new Dictionary<Hash256, bool>();
                var txSignerMap = new Dictionary<Address, int>();
                var startRange = (int)currentSpec.EpochLength - 1;

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
                    penaltiesParole.TryGetValue(penalty, out var epochs);
                    if (epochs == currentSpec.LimitPenaltyEpoch + 1)
                    {
                        txSignerMap.TryGetValue(penalty, out var signedCount);
                        if (signedCount >= currentSpec.MinimumSigningTx)
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
