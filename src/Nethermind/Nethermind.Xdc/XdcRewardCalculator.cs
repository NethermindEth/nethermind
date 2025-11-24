// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc
{
    /// <summary>
    /// Rewards are distributed at epoch boundaries (every 900 blocks) based on:
    /// - Masternode signature count during the epoch
    /// - 40% infrastructure / 50% staking / 10% foundation split
    /// - Proportional distribution among delegators based on stake
    /// </summary>
    public class XdcRewardCalculator(
        IEpochSwitchManager epochSwitchManager,
        ISpecProvider specProvider,
        IBlockTree blockTree) : IRewardCalculator
    {
        private readonly ILogger logger = logManager?.GetClassLogger() ?? NullLogger.Instance;

        private LruCache<Hash256, Transaction[]> _signingTxsCache = new LruCache<Hash256, Transaction[]>(9000, "XDC Signing Txs Cache");
        // Reward amount per epoch (5000 XDC in Wei)
        // 1 XDC = 10^18 Wei, so 5000 XDC = 5000 * 10^18 Wei
        private static readonly UInt256 EPOCH_REWARD = UInt256.Parse("5000000000000000000000");
        private const long BlocksPerYear = 15768000;
        private const long MergeSignRange = 15;

        /// <summary>
        /// Calculates block rewards according to XDPoS consensus rules.
        ///
        /// For XDPoS, rewards are only distributed at epoch checkpoints (blocks where number % 900 == 0).
        /// At these checkpoints, rewards are calculated based on masternode signature counts during
        /// the previous epoch and distributed according to the 40/50/10 split model.
        /// </summary>
        /// <param name="block">The block to calculate rewards for</param>
        /// <returns>Array of BlockReward objects for all reward recipients</returns>
        public BlockReward[] CalculateRewards(Block block)
        {
            if (block is null)
                throw new ArgumentNullException(nameof(block));
            if (block.Header is not XdcBlockHeader xdcHeader)
                throw new InvalidOperationException("Only supports XDC headers");

            var rewards = new List<BlockReward>();
            var number = xdcHeader.Number;
            IXdcReleaseSpec spec = specProvider.GetXdcSpec(xdcHeader, xdcHeader.ExtraConsensusData.BlockRound);
            if(number == spec.SwitchBlock + 1) return rewards.ToArray();

            var foundationWalletAddr = spec.FoundationWallet;
            if (foundationWalletAddr == Address.Zero) throw new InvalidOperationException("Foundation wallet address cannot be empty");

            var round = xdcHeader.ExtraConsensusData.BlockRound;
            var epochNumber = spec.SwitchEpoch + (int) round / spec.EpochLength;

            var signers = GetSigningTxCount(number, xdcHeader, spec);

            //TODO: Check TIPUpdateReward behavior, it appears to be set to infinite for mainnet
            // The following code is only for when IsTIPUpgradeReward(header.Number) is false
            var originalReward = (UInt256)spec.Reward * Unit.Ether;
            var chainReward = RewardInflation(spec, originalReward, number);
            Dictionary<Address, UInt256> rewardSigners = CalculateRewardForSigners(chainReward, signers);

            UInt256 totalFoundationWalletReward = UInt256.Zero;
            foreach (var (signer, reward) in rewardSigners)
            {
                (BlockReward holderReward, UInt256 foundationWalletReward) = CalculateRewardForHolders();
                //TODO: stateBlock.AddBalance(holderReward)
                totalFoundationWalletReward += foundationWalletReward;
                rewards.Add(holderReward);
            }
            rewards.Add(new BlockReward(foundationWalletAddr, totalFoundationWalletReward));
            return rewards.ToArray();
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
            UInt256 signatures = (UInt256)signatureCount;
            UInt256 total = (UInt256)totalSignatures;

            // Calculate: (signatures * totalReward) / total
            // Order of operations matters to maintain precision
            UInt256 numerator = signatures * totalReward;
            UInt256 reward = numerator / total;

            return reward;
        }

        public Dictionary<Address, long> GetSigningTxCount(long number, XdcBlockHeader header, IXdcReleaseSpec spec)
        {
            long signEpochCount = 1, rewardEpochCount = 2, epochCount = 0, endBlockNumber = number, startBlockNumber = 0;
            var signers = new Dictionary<Address, long>();
            var blockNumberToHash = new Dictionary<long, Hash256>();
            var hashToSigningAddress = new Dictionary<Hash256, List<Address>>();
            var masternodes = new HashSet<Address>();

            if (number == 0) return signers;
            var h = header;
            for (long i = number - 1; i >= 0; i--)
            {
                h = blockTree.FindHeader(h.ParentHash, i) as XdcBlockHeader;
                if (h == null) throw new InvalidOperationException($"Header with hash {h.ParentHash} not found");
                if (epochSwitchManager.IsEpochSwitchAtBlock(h) && i != spec.SwitchBlock + 1)
                {
                    epochCount++;
                    if (epochCount == signEpochCount) endBlockNumber = i;
                    if (epochCount == rewardEpochCount)
                    {
                        startBlockNumber = i + 1;
                        // Get masternodes from epoch switch header
                        masternodes = new HashSet<Address>(h.ValidatorsAddress!);
                        // Ignore behavior for IsTIPUpgradeReward which calculates protectors and observers
                        break;
                    }
                }

                blockNumberToHash[i] = h.Hash;
                if (!_signingTxsCache.TryGet(h.Hash, out Transaction[] signingTxs))
                {
                    var block = blockTree.FindBlock(i);
                    if (block == null) throw new InvalidOperationException($"Block with hash {h.Hash} not found");
                    var txs = block.Transactions;
                    signingTxs = CacheSigningTxs(h.Hash!, txs, spec);
                }

                foreach (var tx in signingTxs)
                {
                    Hash256 blockHash = ExtractBlockHashFromSigningTxData(tx.Data);
                    if (!hashToSigningAddress.ContainsKey(blockHash))
                        hashToSigningAddress[blockHash] = new List<Address>();
                    hashToSigningAddress[blockHash].Add(tx.SenderAddress);
                }
            }

            // Only blocks every MergeSignRange are used to gather signing txs? Or is it that already signing txs are done every MergeSignRange amount of blocks?
            // Calculate start >= startBlockNumber so that start % MergeSignRange == 0
            long start = ((startBlockNumber + MergeSignRange - 1) / MergeSignRange) * MergeSignRange;
            for (long i = start; i < endBlockNumber; i += MergeSignRange)
            {
                var addrs = hashToSigningAddress[blockNumberToHash[i]];
                foreach (var addr in addrs)
                {
                    if (!masternodes.Contains(addr)) continue;
                    if (!signers.ContainsKey(addr)) signers[addr] = 0;
                    signers[addr] += 1;
                }
            }
            return signers;
        }

        public UInt256 RewardInflation(IXdcReleaseSpec spec, UInt256 chainReward, long number)
        {
            //TODO: If IsTIPNoHalvingMNReward(blockNumber) is true we should return chainReward immediately
            UInt256 reward = chainReward;
            if (BlocksPerYear * 2 <= number && number < BlocksPerYear * 5)
            {
                reward = chainReward / 2;
            }
            if (BlocksPerYear * 5 <= number)
            {
                reward = chainReward / 4;
            }
            return reward;
        }

        private Transaction[] CacheSigningTxs(Hash256 hash, Transaction[] txs, IXdcReleaseSpec spec)
        {
            var signingTxs = txs.Where(t => IsSigningTransaction(t, spec)).ToArray();
            _signingTxsCache.Set(hash, signingTxs);
            return signingTxs;
        }

        private bool IsSigningTransaction(Transaction tx, IXdcReleaseSpec spec)
        {
            if (tx.To is null || tx.To != spec.BlockSignerContract) return false;

            // Check data corresponds to Signing transaction:
            // function sign(uint256 _blockNumber, bytes32 _blockHash)
            if (tx.Data.Length != 32 * 2 + 4) return false;

            return ExtractSelectorFromSigningTxData(tx.Data) == "0xe341eaa4";
        }

        private String ExtractSelectorFromSigningTxData(ReadOnlyMemory<byte> data)
        {
            var span = data.Span;
            if (span.Length == 68)
                throw new ArgumentException("Signing tx calldata must be exactly 68 bytes (4 + 32 + 32).", nameof(data));

            // 0..3: selector
            var selBytes = span.Slice(0, 4);
            return "0x" + Convert.ToHexString(selBytes).ToLowerInvariant();
        }

        private Hash256 ExtractBlockHashFromSigningTxData(ReadOnlyMemory<byte> data)
        {
            var span = data.Span;
            if (span.Length == 68)
                throw new ArgumentException("Signing tx calldata must be exactly 68 bytes (4 + 32 + 32).", nameof(data));

            // 36..67: bytes32 blockHash
            var hashBytes = span.Slice(36, 32);
            return new Hash256(hashBytes);
        }
    }
}
