// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class PenaltyHandler(IBlockTree tree, ISpecProvider specProvider, IEpochSwitchManager epochSwitchManager, ISigningTxCache signingTxCache) : IPenaltyHandler
{
    private static readonly EthereumEcdsa _ethereumEcdsa = new(0);
    private readonly XdcHeaderDecoder _xdcHeaderDecoder = new();

    public Address[] GetPenalties(XdcBlockHeader header) => epochSwitchManager.GetEpochSwitchInfo(header)?.Penalties ?? [];

    private Address[] GetPreviousPenalties(Hash256 currentHash, IXdcReleaseSpec spec, ulong limit)
    {
        EpochSwitchInfo currentEpochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHash);
        if (currentEpochSwitchInfo is null) return [];

        if (limit == 0) return currentEpochSwitchInfo.Penalties;

        var epochNumber = (ulong)spec.SwitchEpoch + currentEpochSwitchInfo.EpochSwitchBlockInfo.Round / (ulong)spec.EpochLength;
        if (epochNumber < limit) return [];

        var results = epochSwitchManager.GetBlockByEpochNumber(epochNumber - limit);
        if (results is null) return [];

        var header = (XdcBlockHeader)tree.FindHeader(results.Hash, results.BlockNumber);
        if (header is null || header.PenaltiesAddress is null) return [];

        return [.. header.PenaltiesAddress];
    }

    public Address[] HandlePenalties(long number, Hash256 currentHash, Address[] candidates)
    {
        List<Hash256> listBlockHash = [currentHash];
        Dictionary<Address, int> minerStatistics = new();

        long parentNumber = number - 1;
        Hash256 parentHash = currentHash;
        ulong round = 0;

        for (int timeout = 0; ; timeout++)
        {
            var parentHeader = (XdcBlockHeader)tree.FindHeader(parentHash, parentNumber);
            if (parentHeader is not null)
            {
                IXdcReleaseSpec spec = specProvider.GetXdcSpec(parentHeader);
                round = parentHeader.GetRoundNumber(spec);
                break;
            }

            Task.Delay(1 * 1000).Wait();
            if (timeout > 30) return [];
        }

        while (parentNumber >= 0)
        {
            var parentHeader = (XdcBlockHeader)tree.FindHeader(parentHash, parentNumber);
            if (parentHeader is null) return [];

            var isEpochSwitch = epochSwitchManager.IsEpochSwitchAtBlock(parentHeader);
            if (isEpochSwitch)
                break;

            Address miner = parentHeader.Beneficiary ?? _ethereumEcdsa.RecoverAddress(new Signature(parentHeader.Validator.AsSpan(0, 64), parentHeader.Validator[64]), Keccak.Compute(_xdcHeaderDecoder.Encode(parentHeader, RlpBehaviors.ForSealing).Bytes));
            minerStatistics[miner] = minerStatistics.TryGetValue(miner, out int count) ? count + 1 : 1;

            parentNumber--;
            parentHash = parentHeader.ParentHash;
            listBlockHash.Add(parentHash);
        }

        IXdcReleaseSpec currentSpec = specProvider.GetXdcSpec(number, round);
        Address[] preMasternodes = epochSwitchManager.GetEpochSwitchInfo(currentHash)!.Masternodes;
        var penalties = new HashSet<Address>();

        int minMinerBlockPerEpoch = XdcConstants.MinimunMinerBlockPerEpoch;
        if (currentSpec.TipUpgradePenalty <= number)
        {
            minMinerBlockPerEpoch = currentSpec.MinimumMinerBlockPerEpoch;
        }

        foreach (var (miner, total) in minerStatistics)
        {
            if (total < minMinerBlockPerEpoch)
                penalties.Add(miner);
        }
        penalties.UnionWith(
            preMasternodes.Where(address => !minerStatistics.ContainsKey(address))
        );

        bool isTipUpgradePenalty = currentSpec.TipUpgradePenalty <= number;
        if (!isTipUpgradePenalty)
        {
            long comebackHeight = (currentSpec.LimitPenaltyEpochV2 + 1) * currentSpec.EpochLength + currentSpec.SwitchBlock;
            var penComebacks = new HashSet<Address>();

            if (number > comebackHeight)
            {
                Address[] prevPenalties = GetPreviousPenalties(currentHash, currentSpec, (ulong)currentSpec.LimitPenaltyEpochV2);
                penComebacks = prevPenalties.Intersect(candidates).ToHashSet();

                var blockHashes = new HashSet<Hash256>();
                var startRange = Math.Min((int)currentSpec.RangeReturnSigner, listBlockHash.Count) - 1;

                for (int i = startRange; i >= 0; i--)
                {
                    if (penComebacks.Count == 0)
                        break;

                    long blockNumber = number - i - 1;
                    Hash256 blockHash = listBlockHash[i];

                    if (blockNumber % currentSpec.MergeSignRange == 0)
                        blockHashes.Add(blockHash);

                    Transaction[] signingTxs = signingTxCache.GetSigningTransactions(blockHash, blockNumber, currentSpec);
                    foreach (Transaction tx in signingTxs)
                    {
                        var signedBlockHash = new Hash256(tx.Data.Span[^32..]);
                        Address fromSigner = tx.SenderAddress;

                        if (blockHashes.Contains(signedBlockHash))
                            penComebacks.Remove(fromSigner);
                    }
                }

                penalties.UnionWith(penComebacks);
            }
        }
        else
        {
            long limitPenaltyEpoch = currentSpec.LimitPenaltyEpoch > 0 ? currentSpec.LimitPenaltyEpoch : 1;
            long comebackHeight = limitPenaltyEpoch * currentSpec.EpochLength + currentSpec.SwitchBlock;
            if (number > comebackHeight)
            {
                Dictionary<Address, ulong> penaltyParolees = new();
                Address[] lastPenalty = [];

                for (long i = 0; i < limitPenaltyEpoch; i++)
                {
                    Address[] previousPenalties = GetPreviousPenalties(currentHash, currentSpec, (ulong)i);
                    foreach (Address previousPenalty in previousPenalties)
                    {
                        penaltyParolees[previousPenalty] = penaltyParolees.TryGetValue(previousPenalty, out var count)
                            ? count + 1
                            : 1;
                    }

                    if (i == 0) lastPenalty = previousPenalties;
                }

                var blockHashes = new HashSet<Hash256>();
                var txSignerMap = new Dictionary<Address, int>();
                var startRange = Math.Min(currentSpec.EpochLength, listBlockHash.Count) - 1;

                for (int i = startRange; i >= 0; i--)
                {
                    long blockNumber = number - i - 1;
                    Hash256 blockHash = listBlockHash[i];

                    if (blockNumber % currentSpec.MergeSignRange == 0)
                        blockHashes.Add(blockHash);

                    Transaction[] signingTxs = signingTxCache.GetSigningTransactions(blockHash, blockNumber, currentSpec);
                    foreach (Transaction tx in signingTxs)
                    {
                        var signedBlockHash = new Hash256(tx.Data.Span[^32..]);
                        Address fromSigner = tx.SenderAddress!;
                        if (blockHashes.Contains(signedBlockHash))
                        {
                            txSignerMap[fromSigner] = txSignerMap.TryGetValue(fromSigner, out var count) ? count + 1 : 1;
                        }
                    }
                }

                foreach (Address penalty in lastPenalty)
                {
                    penaltyParolees.TryGetValue(penalty, out var epochs);
                    if (epochs == (ulong)limitPenaltyEpoch)
                    {
                        txSignerMap.TryGetValue(penalty, out var signedCount);
                        if (signedCount >= currentSpec.MinimumSigningTx)
                            continue;
                    }
                    penalties.Add(penalty);
                }
            }
        }

        return penalties.ToArray();
    }
}
