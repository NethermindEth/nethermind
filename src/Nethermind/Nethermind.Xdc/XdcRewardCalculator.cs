// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc;

/// <summary>
/// Reward model:
/// - Rewards are paid only at epoch checkpoints.
/// - Pre-upgrade: proportional split of spec.Reward across masternode signatures.
/// - TIP-upgrade: fixed per-signer rewards for masternode/protector/observer.
/// - Holder split remains 90% owner, 10% foundation for each signer reward.
/// </summary>
public class XdcRewardCalculator(IEpochSwitchManager epochSwitchManager,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IMasternodeVotingContract masternodeVotingContract,
    IMintedRecordContract mintedRecordContract,
    ISigningTxCache signingTxCache,
    ITransactionProcessor transactionProcessor,
    IRewardsStore rewardsStore) : IRewardCalculator
{
    private readonly EthereumEcdsa _ethereumEcdsa = new(specProvider.ChainId);
    private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly IMasternodeVotingContract _masternodeVotingContract = masternodeVotingContract;
    private readonly IMintedRecordContract _mintedRecordContract = mintedRecordContract;
    private readonly ISigningTxCache _signingTxCache = signingTxCache;
    private readonly ITransactionProcessor _transactionProcessor = transactionProcessor;
    private readonly IRewardsStore _rewardsStore = rewardsStore;

    /// <summary>
    /// Calculates block rewards according to XDPoS consensus rules.
    ///
    /// For XDPoS, rewards are only distributed at epoch checkpoints.
    /// At these checkpoints, rewards are calculated based on masternode signature counts during
    /// the previous epoch and distributed according to the 90/10 split model.
    /// </summary>
    /// <param name="block">The block to calculate rewards for</param>
    /// <returns>Array of BlockReward objects for all reward recipients</returns>
    public BlockReward[] CalculateRewards(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.Header is not XdcBlockHeader xdcHeader)
            throw new InvalidOperationException("Only supports XDC headers");
        if (xdcHeader.ProcessedRewards is not null)
            return xdcHeader.ProcessedRewards.BlockRewards;

        if (xdcHeader.Number == 0)
            return (xdcHeader.ProcessedRewards = XdcProcessedRewards.Empty).BlockRewards;

        // Rewards in XDC are calculated only if it's an epoch switch block
        if (!_epochSwitchManager.IsEpochSwitchAtBlock(xdcHeader))
            return (xdcHeader.ProcessedRewards = XdcProcessedRewards.Empty).BlockRewards;

        ulong number = xdcHeader.Number;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, xdcHeader.ExtraConsensusData.BlockRound);
        if (number == spec.SwitchBlock + 1)
            return (xdcHeader.ProcessedRewards = XdcProcessedRewards.Empty).BlockRewards;

        Address foundationWalletAddr = spec.FoundationWallet;
        if (foundationWalletAddr == default || foundationWalletAddr == Address.Zero) throw new InvalidOperationException("Foundation wallet address cannot be empty");

        UInt256 totalFoundationWalletReward = UInt256.Zero;
        UInt256 totalMintedInEpoch = UInt256.Zero;
        List<BlockReward> rewards = [];
        XdcEpochRewards rpcRewards = new();
        (Dictionary<Address, XdcRewardLog> masternodeSigners, Dictionary<Address, XdcRewardLog> protectorSigners, Dictionary<Address, XdcRewardLog> observerSigners, UInt256 burnedInOneEpoch) = GetSigningTxCount(xdcHeader, spec);
        CopySigners(masternodeSigners, rpcRewards.Signers);

        if (!spec.IsTipUpgradeRewardEnabled)
        {
            UInt256 chainReward = ApplyRewardInflation((UInt256)spec.Reward * Unit.Ether, number);
            Dictionary<Address, UInt256> rewardSigners = CalculateRewardForSigners(chainReward, masternodeSigners);
            AddDistributedRewards(foundationWalletAddr, rewardSigners, rewards, ref totalFoundationWalletReward, ref totalMintedInEpoch, rpcRewards.Rewards);
        }
        else
        {
            CopySigners(protectorSigners, rpcRewards.SignersProtector);
            CopySigners(observerSigners, rpcRewards.SignersObserver);

            Dictionary<Address, UInt256> masternodeRewards = CalculateFixedRewardForSigners(
                ApplyRewardInflation(spec.MasternodeReward, number),
                masternodeSigners);
            Dictionary<Address, UInt256> protectorRewards = CalculateFixedRewardForSigners(
                ApplyRewardInflation(spec.ProtectorReward, number),
                protectorSigners);
            Dictionary<Address, UInt256> observerRewards = CalculateFixedRewardForSigners(
                ApplyRewardInflation(spec.ObserverReward, number),
                observerSigners);

            AddDistributedRewards(foundationWalletAddr, masternodeRewards, rewards, ref totalFoundationWalletReward, ref totalMintedInEpoch, rpcRewards.Rewards);
            AddDistributedRewards(foundationWalletAddr, protectorRewards, rewards, ref totalFoundationWalletReward, ref totalMintedInEpoch, rpcRewards.RewardsProtector);
            AddDistributedRewards(foundationWalletAddr, observerRewards, rewards, ref totalFoundationWalletReward, ref totalMintedInEpoch, rpcRewards.RewardsObserver);

            _mintedRecordContract.UpdateAccounting(
                _transactionProcessor,
                xdcHeader,
                spec,
                totalMintedInEpoch,
                burnedInOneEpoch);
        }

        if (totalFoundationWalletReward > UInt256.Zero) rewards.Add(new BlockReward(foundationWalletAddr, totalFoundationWalletReward));

        BlockReward[] finalRewards = rewards.ToArray();
        xdcHeader.ProcessedRewards = new XdcProcessedRewards(finalRewards, rpcRewards);
        _rewardsStore.SaveEpochRewards(xdcHeader.Hash!, rpcRewards);

        return finalRewards;
    }

    private (
        Dictionary<Address, XdcRewardLog> MasternodeSigners,
        Dictionary<Address, XdcRewardLog> ProtectorSigners,
        Dictionary<Address, XdcRewardLog> ObserverSigners,
        UInt256 BurnedInOneEpoch) GetSigningTxCount(XdcBlockHeader epochHeader, IXdcReleaseSpec spec)
    {
        Dictionary<Address, XdcRewardLog> masternodeSigners = [];
        Dictionary<Address, XdcRewardLog> protectorSigners = [];
        Dictionary<Address, XdcRewardLog> observerSigners = [];
        UInt256 burnedInOneEpoch = UInt256.Zero;
        ulong number = epochHeader.Number;
        if (number == 0) return (masternodeSigners, protectorSigners, observerSigners, burnedInOneEpoch);

        ulong signEpochCount = 1, rewardEpochCount = 2, epochCount = 0, endBlockNumber = 0, startBlockNumber = 0;

        Dictionary<ulong, Hash256> blockNumberToHash = [];
        Dictionary<Hash256, HashSet<Address>> hashToSigningAddress = [];
        HashSet<Address> masternodes = [];
        HashSet<Address> protectors = [];
        HashSet<Address> observers = [];
        ulong mergeSignRange = spec.MergeSignRange;

        XdcBlockHeader h = epochHeader;
        ulong blockIdx = number - 1;
        while (true)
        {
            Hash256 parentHash = h.ParentHash;
            h = (XdcBlockHeader)_blockTree.FindHeader(parentHash!, blockIdx) ?? throw new InvalidOperationException($"Header with hash {parentHash} not found");
            if (epochCount == 0 && !h.BaseFeePerGas.IsZero)
            {
                UInt256 burnedInBlock = h.BaseFeePerGas * (UInt256)h.GasUsed;
                burnedInOneEpoch += burnedInBlock;
            }
            if (_epochSwitchManager.IsEpochSwitchAtBlock(h) && h.Number != spec.SwitchBlock + 1)
            {
                epochCount++;
                if (epochCount == signEpochCount) endBlockNumber = blockIdx;
                if (epochCount == rewardEpochCount)
                {
                    startBlockNumber = blockIdx + 1;
                    masternodes = GetRewardMasternodes(h, spec);

                    if (spec.IsTipUpgradeRewardEnabled)
                    {
                        // TIPUpgradeReward path: select protector and observer sets from stake-sorted candidates.
                        // Exclude current masternodes and penalized nodes from the checkpoint header.
                        Address[] candidatesByStake = GetCandidatesByStakeForReward(epochHeader);
                        int penaltiesCount = h.PenaltiesAddress?.Length ?? 0;
                        HashSet<Address> excludedCandidates = new(masternodes.Count + penaltiesCount);
                        excludedCandidates.UnionWith(masternodes);
                        if (h.PenaltiesAddress is not null)
                            excludedCandidates.UnionWith(h.PenaltiesAddress);

                        foreach (Address candidate in candidatesByStake)
                        {
                            if (candidate == Address.Zero || excludedCandidates.Contains(candidate))
                                continue;

                            if (protectors.Count < spec.MaxProtectorNodes)
                                protectors.Add(candidate);
                            else if (observers.Count < spec.MaxObserverNodes)
                                observers.Add(candidate);
                        }
                    }
                    break;
                }
            }

            blockNumberToHash[blockIdx] = h.Hash;
            Transaction[] signingTxs = _signingTxCache.GetSigningTransactions(h.Hash, blockIdx, spec);

            foreach (Transaction tx in signingTxs)
            {
                Hash256 blockHash = ExtractBlockHashFromSigningTxData(tx.Data);
                tx.SenderAddress ??= _ethereumEcdsa.RecoverAddress(tx);
                if (!hashToSigningAddress.ContainsKey(blockHash))
                    hashToSigningAddress[blockHash] = [];
                hashToSigningAddress[blockHash].Add(tx.SenderAddress);
            }

            if (blockIdx == 0) break;
            blockIdx--;
        }

        // Only blocks at heights that are multiples of MergeSignRange are considered.
        // Calculate start >= startBlockNumber so that start % MergeSignRange == 0
        ulong start = (startBlockNumber + mergeSignRange - 1) / mergeSignRange * mergeSignRange;
        for (ulong i = start; i <= endBlockNumber; i += mergeSignRange)
        {
            if (!blockNumberToHash.TryGetValue(i, out Hash256 blockHash)) continue;
            if (!hashToSigningAddress.TryGetValue(blockHash, out HashSet<Address> addresses)) continue;
            foreach (Address addr in addresses)
            {
                if (masternodes.Contains(addr))
                    IncrementSignerCount(masternodeSigners, addr);
                else if (protectors.Contains(addr))
                    IncrementSignerCount(protectorSigners, addr);
                else if (observers.Contains(addr))
                    IncrementSignerCount(observerSigners, addr);
            }
        }
        return (masternodeSigners, protectorSigners, observerSigners, burnedInOneEpoch);
    }

    protected internal virtual HashSet<Address> GetRewardMasternodes(XdcBlockHeader checkpointHeader, IXdcReleaseSpec spec)
    {
        if (checkpointHeader.Number <= spec.SwitchBlock)
        {
            return [.. checkpointHeader.ExtraData.ParseV1Masternodes()];
        }

        return [.. checkpointHeader.ValidatorsAddress!];
    }

    private Address[] GetCandidatesByStakeForReward(XdcBlockHeader checkpointHeader)
    {
        // We intentionally avoid GetCandidatesByStake here to preserve Go-equivalent ordering:
        // fetch candidates at the checkpoint header and apply a stable stake-descending sort locally.
        Address[] candidates = _masternodeVotingContract.GetCandidates(_transactionProcessor, checkpointHeader) ?? [];
        if (candidates.Length == 0)
            return [];

        List<CandidateStake> candidatesAndStake = new(candidates.Length);
        foreach (Address candidate in candidates)
        {
            if (candidate == Address.Zero)
                continue;

            candidatesAndStake.Add(new CandidateStake
            {
                Address = candidate,
                Stake = _masternodeVotingContract.GetCandidateStake(_transactionProcessor, checkpointHeader, candidate),
            });
        }

        return candidatesAndStake
            .OrderByDescending(static x => x.Stake)
            .Select(static x => x.Address)
            .ToArray();
    }

    private static void IncrementSignerCount(Dictionary<Address, XdcRewardLog> signers, Address addr)
    {
        if (signers.TryGetValue(addr, out XdcRewardLog? rewardLog))
            rewardLog.Sign++;
        else
            signers[addr] = new XdcRewardLog { Sign = 1 };
    }

    private Hash256 ExtractBlockHashFromSigningTxData(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length != XdcConstants.SignTransactionDataLength)
            throw new ArgumentException($"Signing tx calldata must be exactly {XdcConstants.SignTransactionDataLength} bytes.", nameof(data));

        // 36..67: bytes32 blockHash
        ReadOnlySpan<byte> hashBytes = span.Slice(36, 32);
        return new Hash256(hashBytes);
    }

    private Dictionary<Address, UInt256> CalculateRewardForSigners(UInt256 totalReward,
        Dictionary<Address, XdcRewardLog> signers)
    {
        Dictionary<Address, UInt256> rewardSigners = [];
        ulong totalSigningCount = 0;
        foreach (XdcRewardLog signer in signers.Values)
            totalSigningCount += signer.Sign;

        foreach ((Address signer, XdcRewardLog rewardLog) in signers)
        {
            UInt256 reward = CalculateProportionalReward(rewardLog.Sign, totalSigningCount, totalReward);
            rewardLog.Reward = reward.ToString();
            rewardSigners.Add(signer, reward);
        }
        return rewardSigners;
    }

    private Dictionary<Address, UInt256> CalculateFixedRewardForSigners(
        UInt256 rewardPerSigner,
        Dictionary<Address, XdcRewardLog> signers)
    {
        Dictionary<Address, UInt256> rewardSigners = [];
        foreach ((Address signer, XdcRewardLog rewardLog) in signers)
        {
            rewardLog.Reward = rewardPerSigner.ToString();
            rewardSigners[signer] = rewardPerSigner;
        }

        return rewardSigners;
    }

    /// <summary>
    /// Calculates a proportional reward based on the number of signatures.
    /// Uses UInt256 arithmetic to maintain precision with large Wei values.
    ///
    /// Formula: (totalReward / totalSignatures) * signatureCount
    /// </summary>
    internal UInt256 CalculateProportionalReward(
        ulong signatureCount,
        ulong totalSignatures,
        UInt256 totalReward)
    {
        if (signatureCount == 0 || totalSignatures == 0)
        {
            return UInt256.Zero;
        }

        UInt256 signatures = (UInt256)signatureCount;
        UInt256 total = (UInt256)totalSignatures;

        UInt256 portion = totalReward / total;
        UInt256 reward = portion * signatures;

        return reward;
    }

    internal (BlockReward HolderReward, UInt256 FoundationWalletReward) DistributeRewards(
        Address masternodeAddress, UInt256 reward, Address foundationWalletAddr)
    {
        if (_transactionProcessor is not XdcTransactionProcessor xdcTransactionProcessor)
            throw new InvalidOperationException($"{nameof(XdcRewardCalculator)} requires {nameof(XdcTransactionProcessor)}.");

        Address owner = _masternodeVotingContract.GetCandidateOwner(xdcTransactionProcessor.RewardWorldState, masternodeAddress);

        // 90% of the reward goes to the masternode
        UInt256 masterReward = reward * 90 / 100;

        // 10% of the reward goes to the foundation wallet
        UInt256 foundationReward = reward / 10;

        // The reference client stores both entries in a map, so the foundation reward replaces the owner reward on collision.
        if (owner == foundationWalletAddr)
        {
            return (new BlockReward(owner, foundationReward), UInt256.Zero);
        }

        return (new BlockReward(owner, masterReward), foundationReward);
    }

    private void AddDistributedRewards(
        Address foundationWalletAddr,
        Dictionary<Address, UInt256> rewardSigners,
        List<BlockReward> rewards,
        ref UInt256 totalFoundationWalletReward,
        ref UInt256 totalMintedInEpoch,
        Dictionary<string, Dictionary<string, string>> rpcRewards)
    {
        foreach ((Address signer, UInt256 reward) in rewardSigners)
        {
            (BlockReward holderReward, UInt256 foundationWalletReward) = DistributeRewards(signer, reward, foundationWalletAddr);
            totalFoundationWalletReward += foundationWalletReward;
            totalMintedInEpoch += holderReward.Value + foundationWalletReward;
            rewards.Add(holderReward);

            string signerKey = signer.ToString();
            if (!rpcRewards.TryGetValue(signerKey, out Dictionary<string, string>? holdersMap))
            {
                holdersMap = [];
                rpcRewards[signerKey] = holdersMap;
            }

            holdersMap[holderReward.Address.ToString()] = holderReward.Value.ToString();
            holdersMap[foundationWalletAddr.ToString()] = foundationWalletReward.ToString();
        }
    }

    private static void CopySigners(Dictionary<Address, XdcRewardLog> source, Dictionary<string, XdcRewardLog> destination)
    {
        foreach ((Address signer, XdcRewardLog rewardLog) in source)
        {
            destination[signer.ToString()] = rewardLog;
        }
    }

    private static UInt256 ApplyRewardInflation(UInt256 reward, ulong number)
    {
        if (XdcConstants.BlocksPerYear * 2 <= number && number < XdcConstants.BlocksPerYear * 5)
            return reward / 2;

        if (XdcConstants.BlocksPerYear * 5 <= number)
            return reward / 4;

        return reward;
    }
}
