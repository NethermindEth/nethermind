// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using Nethermind.Crypto;
using Nethermind.Xdc.Contracts;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Xdc
{
    /// <summary>
    /// Reward model:
    /// - Rewards are paid only at epoch checkpoints (number % EpochLength == 0).
    /// - Pre-upgrade: proportional split of spec.Reward across masternode signatures.
    /// - TIP-upgrade: fixed per-signer rewards for masternode/protector/observer.
    /// - Holder split remains 90% owner, 10% foundation for each signer reward.
    /// </summary>
    public class XdcRewardCalculator : IRewardCalculator
    {
        // XDC rule: signing transactions are sampled/merged every N blocks (N=15 on XDC).
        // Only block numbers that are multiples of MergeSignRange are considered when tallying signers.
        private readonly EthereumEcdsa _ethereumEcdsa;
        private readonly IEpochSwitchManager _epochSwitchManager;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IMasternodeVotingContract _masternodeVotingContract;
        private readonly IMintedRecordContract _mintedRecordContract;
        private readonly ISigningTxCache _signingTxCache;
        private readonly ITransactionProcessor _transactionProcessor;

        public XdcRewardCalculator(
            IEpochSwitchManager epochSwitchManager,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IMasternodeVotingContract masternodeVotingContract,
            IMintedRecordContract mintedRecordContract,
            ISigningTxCache signingTxCache,
            ITransactionProcessor transactionProcessor)
        {
            _ethereumEcdsa = new EthereumEcdsa(specProvider.ChainId);
            _epochSwitchManager = epochSwitchManager;
            _specProvider = specProvider;
            _blockTree = blockTree;
            _masternodeVotingContract = masternodeVotingContract;
            _mintedRecordContract = mintedRecordContract;
            _signingTxCache = signingTxCache;
            _transactionProcessor = transactionProcessor;
        }
        /// <summary>
        /// Calculates block rewards according to XDPoS consensus rules.
        ///
        /// For XDPoS, rewards are only distributed at epoch checkpoints (blocks where number % 900 == 0).
        /// At these checkpoints, rewards are calculated based on masternode signature counts during
        /// the previous epoch and distributed according to the 90/10 split model.
        /// </summary>
        /// <param name="block">The block to calculate rewards for</param>
        /// <returns>Array of BlockReward objects for all reward recipients</returns>
        public BlockReward[] CalculateRewards(Block block)
        {
            if (block is null)
                throw new ArgumentNullException(nameof(block));
            if (block.Header is not XdcBlockHeader xdcHeader)
                throw new InvalidOperationException("Only supports XDC headers");
            if (xdcHeader.Number == 0)
                return Array.Empty<BlockReward>();

            // Rewards in XDC are calculated only if it's an epoch switch block
            if (!_epochSwitchManager.IsEpochSwitchAtBlock(xdcHeader)) return Array.Empty<BlockReward>();

            var number = xdcHeader.Number;
            IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, xdcHeader.ExtraConsensusData.BlockRound);
            if (number == spec.SwitchBlock + 1) return Array.Empty<BlockReward>();

            Address foundationWalletAddr = spec.FoundationWallet;
            if (foundationWalletAddr == default || foundationWalletAddr == Address.Zero) throw new InvalidOperationException("Foundation wallet address cannot be empty");

            UInt256 totalFoundationWalletReward = UInt256.Zero;
            UInt256 totalMintedInEpoch = UInt256.Zero;
            var rewards = new List<BlockReward>();
            var (masternodeSigners, protectorSigners, observerSigners, burnedInOneEpoch) = GetSigningTxCount(xdcHeader, spec);

            bool isTipUpgradeRewardEnabled = xdcHeader.Number >= spec.TipUpgradeRewardBlock;
            if (!isTipUpgradeRewardEnabled)
            {
                UInt256 chainReward = (UInt256)spec.Reward * Unit.Ether;
                Dictionary<Address, UInt256> rewardSigners = CalculateRewardForSigners(chainReward, masternodeSigners);
                AddDistributedRewards(xdcHeader, rewardSigners, rewards, ref totalFoundationWalletReward, ref totalMintedInEpoch);
            }
            else
            {
                Dictionary<Address, UInt256> masternodeRewards = CalculateFixedRewardForSigners(
                    spec.MasternodeReward,
                    masternodeSigners);
                Dictionary<Address, UInt256> protectorRewards = CalculateFixedRewardForSigners(
                    spec.ProtectorReward,
                    protectorSigners);
                Dictionary<Address, UInt256> observerRewards = CalculateFixedRewardForSigners(
                    spec.ObserverReward,
                    observerSigners);

                AddDistributedRewards(xdcHeader, masternodeRewards, rewards, ref totalFoundationWalletReward, ref totalMintedInEpoch);
                AddDistributedRewards(xdcHeader, protectorRewards, rewards, ref totalFoundationWalletReward, ref totalMintedInEpoch);
                AddDistributedRewards(xdcHeader, observerRewards, rewards, ref totalFoundationWalletReward, ref totalMintedInEpoch);

                _mintedRecordContract.UpdateAccounting(
                    _transactionProcessor,
                    xdcHeader,
                    spec,
                    totalMintedInEpoch,
                    burnedInOneEpoch);
            }

            if (totalFoundationWalletReward > UInt256.Zero) rewards.Add(new BlockReward(foundationWalletAddr, totalFoundationWalletReward));
            return rewards.ToArray();
        }

        private (
            Dictionary<Address, long> MasternodeSigners,
            Dictionary<Address, long> ProtectorSigners,
            Dictionary<Address, long> ObserverSigners,
            UInt256 BurnedInOneEpoch) GetSigningTxCount(XdcBlockHeader epochHeader, IXdcReleaseSpec spec)
        {
            var masternodeSigners = new Dictionary<Address, long>();
            var protectorSigners = new Dictionary<Address, long>();
            var observerSigners = new Dictionary<Address, long>();
            UInt256 burnedInOneEpoch = UInt256.Zero;
            long number = epochHeader.Number;
            if (number == 0) return (masternodeSigners, protectorSigners, observerSigners, burnedInOneEpoch);

            long signEpochCount = 1, rewardEpochCount = 2, epochCount = 0, endBlockNumber = 0, startBlockNumber = 0;

            var blockNumberToHash = new Dictionary<long, Hash256>();
            var hashToSigningAddress = new Dictionary<Hash256, HashSet<Address>>();
            var masternodes = new HashSet<Address>();
            var protectors = new HashSet<Address>();
            var observers = new HashSet<Address>();
            var mergeSignRange = spec.MergeSignRange;

            XdcBlockHeader h = epochHeader;
            for (long i = number - 1; i >= 0; i--)
            {
                Hash256 parentHash = h.ParentHash;
                h = _blockTree.FindHeader(parentHash!, i) as XdcBlockHeader;
                if (h == null) throw new InvalidOperationException($"Header with hash {parentHash} not found");
                if (epochCount == 0 && !h.BaseFeePerGas.IsZero)
                {
                    UInt256 burnedInBlock = h.BaseFeePerGas * (UInt256)h.GasUsed;
                    burnedInOneEpoch += burnedInBlock;
                }
                if (_epochSwitchManager.IsEpochSwitchAtBlock(h) && h.Number != spec.SwitchBlock + 1)
                {
                    epochCount++;
                    if (epochCount == signEpochCount) endBlockNumber = i;
                    if (epochCount == rewardEpochCount)
                    {
                        startBlockNumber = i + 1;
                        // Get masternodes from epoch switch header
                        if (h.Number <= spec.SwitchBlock)
                            masternodes = new HashSet<Address>(h.ExtraData.ParseV1Masternodes());
                        else
                            masternodes = new HashSet<Address>(h.ValidatorsAddress!);

                        if (h.Number >= spec.TipUpgradeRewardBlock)
                        {
                            // TIPUpgradeReward path: select protector and observer sets from stake-sorted candidates.
                            // Exclude current masternodes and penalized nodes from the checkpoint header.
                            Address[] candidatesByStake = _masternodeVotingContract.GetCandidatesByStake(h) ?? [];
                            int penaltiesCount = h.PenaltiesAddress?.Length ?? 0;
                            var excludedCandidates = new HashSet<Address>(masternodes.Count + penaltiesCount);
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

                blockNumberToHash[i] = h.Hash;
                Transaction[] signingTxs = _signingTxCache.GetSigningTransactions(h.Hash, i, spec);

                foreach (Transaction tx in signingTxs)
                {
                    Hash256 blockHash = ExtractBlockHashFromSigningTxData(tx.Data);
                    tx.SenderAddress ??= _ethereumEcdsa.RecoverAddress(tx);
                    if (!hashToSigningAddress.ContainsKey(blockHash))
                        hashToSigningAddress[blockHash] = new HashSet<Address>();
                    hashToSigningAddress[blockHash].Add(tx.SenderAddress);
                }
            }

            // Only blocks at heights that are multiples of MergeSignRange are considered.
            // Calculate start >= startBlockNumber so that start % MergeSignRange == 0
            long start = ((startBlockNumber + mergeSignRange - 1) / mergeSignRange) * mergeSignRange;
            for (long i = start; i < endBlockNumber; i += mergeSignRange)
            {
                if (!blockNumberToHash.TryGetValue(i, out var blockHash)) continue;
                if (!hashToSigningAddress.TryGetValue(blockHash, out var addresses)) continue;
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

        private static void IncrementSignerCount(Dictionary<Address, long> signers, Address addr)
        {
            if (!signers.TryAdd(addr, 1))
                signers[addr] += 1;
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
            Dictionary<Address, long> signers)
        {
            var rewardSigners = new Dictionary<Address, UInt256>();
            long totalSigningCount = 0;
            foreach (long signerCount in signers.Values)
            {
                totalSigningCount += signerCount;
            }

            foreach (var (signer, count) in signers)
            {
                UInt256 reward = CalculateProportionalReward(count, totalSigningCount, totalReward);
                rewardSigners.Add(signer, reward);
            }
            return rewardSigners;
        }

        private Dictionary<Address, UInt256> CalculateFixedRewardForSigners(
            UInt256 rewardPerSigner,
            Dictionary<Address, long> signers)
        {
            var rewardSigners = new Dictionary<Address, UInt256>();
            if (rewardPerSigner == UInt256.Zero)
                return rewardSigners;

            foreach ((Address signer, long signerCount) in signers)
            {
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
            long signatureCount,
            long totalSignatures,
            UInt256 totalReward)
        {
            if (signatureCount <= 0 || totalSignatures <= 0)
            {
                return UInt256.Zero;
            }

            // Convert to UInt256 for precision
            var signatures = (UInt256)signatureCount;
            var total = (UInt256)totalSignatures;


            UInt256 portion = totalReward / total;
            UInt256 reward = portion * signatures;

            return reward;
        }

        internal (BlockReward HolderReward, UInt256 FoundationWalletReward) DistributeRewards(
            Address masternodeAddress, UInt256 reward, XdcBlockHeader header)
        {
            Address owner = _masternodeVotingContract.GetCandidateOwner(_transactionProcessor, header, masternodeAddress);

            // 90% of the reward goes to the masternode
            UInt256 masterReward = reward * 90 / 100;

            // 10% of the reward goes to the foundation wallet
            UInt256 foundationReward = reward / 10;

            return (new BlockReward(owner, masterReward), foundationReward);
        }

        private void AddDistributedRewards(
            XdcBlockHeader header,
            Dictionary<Address, UInt256> rewardSigners,
            List<BlockReward> rewards,
            ref UInt256 totalFoundationWalletReward,
            ref UInt256 totalMintedInEpoch)
        {
            foreach ((Address signer, UInt256 reward) in rewardSigners)
            {
                (BlockReward holderReward, UInt256 foundationWalletReward) = DistributeRewards(signer, reward, header);
                totalFoundationWalletReward += foundationWalletReward;
                totalMintedInEpoch += holderReward.Value + foundationWalletReward;
                rewards.Add(holderReward);
            }
        }
    }
}
