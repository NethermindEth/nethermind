// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Crypto;
using Nethermind.Xdc.Contracts;

namespace Nethermind.Xdc
{
    /// <summary>
    /// Reward model (current mainnet):
    /// - Rewards are paid only at epoch checkpoints (number % EpochLength == 0).
    /// - For now we **ignore** TIPUpgradeReward behavior because on mainnet
    ///   the upgrade activation is set far in the future (effectively “not active”).
    ///   When TIPUpgradeReward activates, protector/observer beneficiaries must be added.
    /// - Current split implemented here: 90% to masternode owner, 10% to foundation.
    /// </summary>
    public class XdcRewardCalculator(
        IEpochSwitchManager epochSwitchManager,
        ISpecProvider specProvider,
        IBlockTree blockTree,
        IMasternodeVotingContract masternodeVotingContract) : IRewardCalculator
    {
        private LruCache<Hash256, Transaction[]> _signingTxsCache = new(9000, "XDC Signing Txs Cache");
        private const long BlocksPerYear = 15768000;
        // XDC rule: signing transactions are sampled/merged every N blocks (N=15 on XDC).
        // Only block numbers that are multiples of MergeSignRange are considered when tallying signers.
        private const long MergeSignRange = 15;
        private static readonly EthereumEcdsa _ethereumEcdsa = new(0);

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
            if (!epochSwitchManager.IsEpochSwitchAtBlock(xdcHeader)) return Array.Empty<BlockReward>();

            var number = xdcHeader.Number;
            IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcHeader, xdcHeader.ExtraConsensusData.BlockRound);
            if (number == spec.SwitchBlock + 1) return Array.Empty<BlockReward>();

            Address foundationWalletAddr = spec.FoundationWallet;
            if (foundationWalletAddr == Address.Zero) throw new InvalidOperationException("Foundation wallet address cannot be empty");

            var (signers, count) = GetSigningTxCount(number, xdcHeader, spec);

            UInt256 chainReward = (UInt256)spec.Reward * Unit.Ether;
            Dictionary<Address, UInt256> rewardSigners = CalculateRewardForSigners(chainReward, signers, count);

            UInt256 totalFoundationWalletReward = UInt256.Zero;
            var rewards = new List<BlockReward>();
            foreach (var (signer, reward) in rewardSigners)
            {
                (BlockReward holderReward, UInt256 foundationWalletReward) = DistributeRewards(signer, reward, xdcHeader);
                totalFoundationWalletReward += foundationWalletReward;
                rewards.Add(holderReward);
            }
            if (totalFoundationWalletReward > UInt256.Zero) rewards.Add(new BlockReward(foundationWalletAddr, totalFoundationWalletReward));
            return rewards.ToArray();
        }

        private (Dictionary<Address, long> Signers, long Count) GetSigningTxCount(long number, XdcBlockHeader header, IXdcReleaseSpec spec)
        {
            var signers = new Dictionary<Address, long>();
            if (number == 0) return (signers, 0);

            long signEpochCount = 1, rewardEpochCount = 2, epochCount = 0, endBlockNumber = 0, startBlockNumber = 0, signingCount = 0;
            var blockNumberToHash = new Dictionary<long, Hash256>();
            var hashToSigningAddress = new Dictionary<Hash256, HashSet<Address>>();
            var masternodes = new HashSet<Address>();

            XdcBlockHeader h = header;
            for (long i = number - 1; i >= 0; i--)
            {
                Hash256 parentHash = h.ParentHash;
                h = blockTree.FindHeader(parentHash!, i) as XdcBlockHeader;
                if (h == null) throw new InvalidOperationException($"Header with hash {parentHash} not found");
                if (epochSwitchManager.IsEpochSwitchAtBlock(h) && i != spec.SwitchBlock + 1)
                {
                    epochCount++;
                    if (epochCount == signEpochCount) endBlockNumber = i;
                    if (epochCount == rewardEpochCount)
                    {
                        startBlockNumber = i + 1;
                        // Get masternodes from epoch switch header
                        masternodes = new HashSet<Address>(h.ValidatorsAddress!);
                        // TIPUpgradeReward path (protector/observer selection) is currently ignored,
                        // because on mainnet the upgrade height is set to an effectively unreachable block.
                        // If/when that changes, we must compute protector/observer sets here.
                        break;
                    }
                }

                blockNumberToHash[i] = h.Hash;
                if (!_signingTxsCache.TryGet(h.Hash, out Transaction[] signingTxs))
                {
                    Block? block = blockTree.FindBlock(i);
                    if (block == null) throw new InvalidOperationException($"Block with number {i} not found");
                    Transaction[] txs = block.Transactions;
                    signingTxs = CacheSigningTxs(h.Hash!, txs, spec);
                }

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
            long start = ((startBlockNumber + MergeSignRange - 1) / MergeSignRange) * MergeSignRange;
            for (long i = start; i < endBlockNumber; i += MergeSignRange)
            {
                if (!blockNumberToHash.TryGetValue(i, out var blockHash)) continue;
                if (!hashToSigningAddress.TryGetValue(blockHash, out var addresses)) continue;
                foreach (Address addr in addresses)
                {
                    if (!masternodes.Contains(addr)) continue;
                    if (!signers.ContainsKey(addr)) signers[addr] = 0;
                    signers[addr] += 1;
                    signingCount++;
                }
            }
            return (signers, signingCount);
        }

        private Transaction[] CacheSigningTxs(Hash256 hash, Transaction[] txs, IXdcReleaseSpec spec)
        {
            Transaction[] signingTxs = txs.Where(t => IsSigningTransaction(t, spec)).ToArray();
            _signingTxsCache.Set(hash, signingTxs);
            return signingTxs;
        }

        // Signing transaction ABI (Solidity):
        // function sign(uint256 _blockNumber, bytes32 _blockHash)
        // Calldata = 4-byte selector + 32-byte big-endian uint + 32-byte bytes32 = 68 bytes total.
        private bool IsSigningTransaction(Transaction tx, IXdcReleaseSpec spec)
        {
            if (tx.To is null || tx.To != spec.BlockSignerContract) return false;
            if (tx.Data.Length != 68) return false;

            return ExtractSelectorFromSigningTxData(tx.Data) == "0xe341eaa4";
        }

        private String ExtractSelectorFromSigningTxData(ReadOnlyMemory<byte> data)
        {
            ReadOnlySpan<byte> span = data.Span;
            if (span.Length != 68)
                throw new ArgumentException("Signing tx calldata must be exactly 68 bytes (4 + 32 + 32).", nameof(data));

            // 0..3: selector
            ReadOnlySpan<byte> selBytes = span.Slice(0, 4);
            return "0x" + Convert.ToHexString(selBytes).ToLowerInvariant();
        }

        private Hash256 ExtractBlockHashFromSigningTxData(ReadOnlyMemory<byte> data)
        {
            ReadOnlySpan<byte> span = data.Span;
            if (span.Length != 68)
                throw new ArgumentException("Signing tx calldata must be exactly 68 bytes (4 + 32 + 32).", nameof(data));

            // 36..67: bytes32 blockHash
            ReadOnlySpan<byte> hashBytes = span.Slice(36, 32);
            return new Hash256(hashBytes);
        }

        private Dictionary<Address, UInt256> CalculateRewardForSigners(UInt256 totalReward,
            Dictionary<Address, long> signers, long totalSigningCount)
        {
            var rewardSigners = new Dictionary<Address, UInt256>();
            foreach (var (signer, count) in signers)
            {
                UInt256 reward = CalculateProportionalReward(count, totalSigningCount, totalReward);
                rewardSigners.Add(signer, reward);
            }
            return rewardSigners;
        }

        /// <summary>
        /// Calculates a proportional reward based on the number of signatures.
        /// Uses UInt256 arithmetic to maintain precision with large Wei values.
        ///
        /// Formula: (signatureCount / totalSignatures) * totalReward
        /// </summary>
        private UInt256 CalculateProportionalReward(
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

            // Calculate: (signatures * totalReward) / total
            // Order of operations matters to maintain precision
            UInt256 numerator = signatures * totalReward;
            UInt256 reward = numerator / total;

            return reward;
        }

        private (BlockReward HolderReward, UInt256 FoundationWalletReward) DistributeRewards(
            Address masternodeAddress, UInt256 reward, XdcBlockHeader header)
        {
            Address owner = masternodeVotingContract.GetCandidateOwner(header, masternodeAddress);

            // 90% of the reward goes to the masternode
            UInt256 masterReward = reward * 90 / 100;

            // 10% of the reward goes to the foundation wallet
            UInt256 foundationReward = reward / 10;

            return (new BlockReward(owner, masterReward), foundationReward);
        }
    }
}
