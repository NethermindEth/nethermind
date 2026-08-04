// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

internal class PenaltyHandler(IBlockTree tree, ISpecProvider specProvider, IEpochSwitchManager epochSwitchManager, ISigningTxCache signingTxCache) : IPenaltyHandler
{
    private readonly EthereumEcdsa _ethereumEcdsa = new(specProvider.ChainId);

    public Address[] GetPenalties(XdcBlockHeader header) => epochSwitchManager.GetEpochSwitchInfo(header)?.Penalties ?? [];

    private Address[] GetPreviousPenalties(Hash256 currentHash, IXdcReleaseSpec spec, ulong limit)
    {
        EpochSwitchInfo currentEpochSwitchInfo = epochSwitchManager.GetEpochSwitchInfo(currentHash);
        if (currentEpochSwitchInfo is null) return [];

        if (limit == 0) return currentEpochSwitchInfo.Penalties;

        ulong epochNumber = spec.SwitchEpoch + currentEpochSwitchInfo.EpochSwitchBlockInfo.Round / spec.EpochLength;
        if (epochNumber < limit) return [];

        BlockRoundInfo results = epochSwitchManager.GetBlockByEpochNumber(epochNumber - limit);
        if (results is null) return [];

        XdcBlockHeader header = (XdcBlockHeader)tree.FindHeader(results.Hash, results.BlockNumber);
        if (header?.PenaltiesAddress is null) return [];

        return [.. header.PenaltiesAddress];
    }

    public Address[] HandlePenalties(ulong number, Hash256 parentHash, Address[] candidates)
    {
        List<Hash256> listBlockHash = [parentHash];
        Dictionary<Address, ulong> minerStatistics = [];

        // Walk down to (but not past) block 1; block 0 is genesis and never an epoch switch body block.
        ulong parentNumber = number - 1;
        Hash256 currentHash = parentHash;
        while (parentNumber > 0)
        {
            XdcBlockHeader parentHeader = (XdcBlockHeader)tree.FindHeader(currentHash, parentNumber);
            if (parentHeader is null) return [];

            bool isEpochSwitch = epochSwitchManager.IsEpochSwitchAtBlock(parentHeader);
            if (isEpochSwitch)
                break;

            Address miner = parentHeader.Beneficiary;
            minerStatistics[miner!] = minerStatistics.TryGetValue(miner, out ulong count) ? count + 1 : 1;

            parentNumber--;
            currentHash = parentHeader.ParentHash;
            listBlockHash.Add(currentHash);
        }

        XdcBlockHeader header = (XdcBlockHeader)tree.FindHeader(parentHash, number - 1);
        IXdcReleaseSpec currentSpec = specProvider.GetXdcSpec(header!);
        Address[] preMasternodes = epochSwitchManager.GetEpochSwitchInfo(parentHash)!.Masternodes;
        HashSet<Address> penalties = [];

        ulong minMinerBlockPerEpoch = currentSpec.IsTipUpgradePenaltyEnabled
            ? currentSpec.MinimumMinerBlockPerEpoch
            : XdcConstants.MinimumMinerBlockPerEpoch;

        foreach ((Address miner, ulong total) in minerStatistics)
        {
            if (total < minMinerBlockPerEpoch)
                penalties.Add(miner);
        }
        penalties.UnionWith(
            preMasternodes.Except(minerStatistics.Keys)
        );

        if (!currentSpec.IsTipUpgradePenaltyEnabled)
        {
            ulong comebackHeight = (currentSpec.LimitPenaltyEpochV2 + 1) * currentSpec.EpochLength + currentSpec.SwitchBlock;
            if (number > comebackHeight)
            {
                Address[] prevPenalties = GetPreviousPenalties(parentHash, currentSpec, currentSpec.LimitPenaltyEpochV2);
                HashSet<Address> penComebacks = prevPenalties.Intersect(candidates).ToHashSet();

                HashSet<Hash256> blockHashes = [];
                int startRange = (int)Math.Min(currentSpec.RangeReturnSigner, (ulong)listBlockHash.Count) - 1;

                for (int i = startRange; i >= 0; i--)
                {
                    if (penComebacks.Count == 0)
                        break;

                    ulong blockNumber = number - (ulong)i - 1;
                    Hash256 blockHash = listBlockHash[i];

                    if (blockNumber % currentSpec.MergeSignRange == 0)
                        blockHashes.Add(blockHash);

                    Transaction[] signingTxs = signingTxCache.GetSigningTransactions(blockHash, blockNumber, currentSpec);
                    foreach (Transaction tx in signingTxs)
                    {
                        Hash256 signedBlockHash = new(tx.Data.Span[^32..]);
                        tx.SenderAddress ??= _ethereumEcdsa.RecoverAddress(tx);
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
            // LimitPenaltyEpoch of 0 is treated as 1 to avoid division/multiplication by zero.
            ulong limitPenaltyEpoch = currentSpec.LimitPenaltyEpoch > 0 ? currentSpec.LimitPenaltyEpoch : 1;
            ulong comebackHeight = limitPenaltyEpoch * currentSpec.EpochLength + currentSpec.SwitchBlock;
            if (number > comebackHeight)
            {
                Dictionary<Address, ulong> penaltyParolees = [];
                Address[] lastPenalty = [];

                for (ulong i = 0; i < limitPenaltyEpoch; i++)
                {
                    Address[] previousPenalties = GetPreviousPenalties(parentHash, currentSpec, i);
                    foreach (Address previousPenalty in previousPenalties)
                    {
                        penaltyParolees[previousPenalty] = penaltyParolees.TryGetValue(previousPenalty, out ulong count)
                            ? count + 1
                            : 1;
                    }

                    if (i == 0) lastPenalty = previousPenalties;
                }

                HashSet<Hash256> blockHashes = [];
                Dictionary<Address, ulong> txSignerMap = [];
                int startRange = (int)Math.Min(currentSpec.EpochLength, (ulong)listBlockHash.Count) - 1;

                for (int i = startRange; i >= 0; i--)
                {
                    ulong blockNumber = number - (ulong)i - 1;
                    Hash256 blockHash = listBlockHash[i];

                    if (blockNumber % currentSpec.MergeSignRange == 0)
                        blockHashes.Add(blockHash);

                    Transaction[] signingTxs = signingTxCache.GetSigningTransactions(blockHash, blockNumber, currentSpec);
                    foreach (Transaction tx in signingTxs)
                    {
                        Hash256 signedBlockHash = new(tx.Data.Span[^32..]);
                        tx.SenderAddress ??= _ethereumEcdsa.RecoverAddress(tx);
                        Address fromSigner = tx.SenderAddress;
                        if (blockHashes.Contains(signedBlockHash))
                        {
                            txSignerMap[fromSigner] = txSignerMap.TryGetValue(fromSigner, out ulong count) ? count + 1 : 1;
                        }
                    }
                }

                foreach (Address penalty in lastPenalty)
                {
                    penaltyParolees.TryGetValue(penalty, out ulong epochs);
                    if (epochs == limitPenaltyEpoch)
                    {
                        txSignerMap.TryGetValue(penalty, out ulong signedCount);
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
