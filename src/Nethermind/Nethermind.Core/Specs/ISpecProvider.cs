// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// Provides details of enabled EIPs and other chain parameters at any chain height.
    /// </summary>
    public interface ISpecProvider
    {
        public const ulong TimestampForkNever = ulong.MaxValue;

        /// <summary>
        /// The merge block number is different from the rest forks because we don't know the merge block before it happens.
        /// This function handles change of the merge block
        /// </summary>
        void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null);

        /// <summary>
        /// We have two different block numbers for merge transition:
        /// https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#definitions
        /// 1.FORK_NEXT_VALUE (MergeForkId in chain spec) - we know it before the merge happens. It is included in TransitionsBlocks.
        /// It will affect fork_id calculation for networking.
        /// 2. The real merge block (ISpecProvider.MergeBlockNumber) - the real merge block number. We don't know it before the transition.
        /// It affects all post-merge logic, for example, difficulty opcode, post-merge block rewards.
        /// This block number doesn't affect fork_id calculation and it isn't included in ISpecProvider.TransitionsBlocks
        /// </summary>
        ForkActivation? MergeBlockNumber { get; }

        /// <summary>
        /// Gets the first time the fork is activated by timestamp
        /// </summary>
        ulong TimestampFork { get; }

        UInt256? TerminalTotalDifficulty { get; }

        /// <summary>
        /// Retrieves the list of enabled EIPs at genesis block.
        /// </summary>
        IReleaseSpec GenesisSpec { get; }

        /// <summary>
        /// When true genesis state root calculation is disabled and spec state root is set.
        /// </summary>
        bool GenesisStateUnavailable { get => false; }

        /// <summary>
        /// Block number at which DAO happens (only relevant for mainnet)
        /// </summary>
        long? DaoBlockNumber { get; }

        ulong? BeaconChainGenesisTimestamp { get; }

        /// <summary>
        /// Unique identifier of the chain that allows to sign messages for the specified chain only.
        /// It is also used when verifying if sync peers are on the same chain.
        /// </summary>
        ulong NetworkId { get; }

        /// <summary>
        /// Additional identifier of the chain to mitigate risks described in 155
        /// </summary>
        ulong ChainId { get; }

        /// <summary>
        /// Original engine of the chain
        /// </summary>
        string SealEngine => SealEngineType.Ethash;

        /// <summary>
        /// All block numbers at which a change in spec (a fork) happens.
        /// </summary>
        ForkActivation[] TransitionActivations { get; }

        /// <summary>
        /// Resolves a spec for the given block number.
        /// </summary>
        /// <param name="forkActivation"></param>
        /// <returns>A spec that is valid at the given chain height</returns>
        protected internal IReleaseSpec GetSpec(ForkActivation forkActivation);
    }

    public static class SpecProviderExtensions
    {
        public static IReleaseSpec GetSpec(this ISpecProvider specProvider, ForkActivation forkActivation)
            => specProvider.SealEngine == SealEngineType.AuRa
                ? new AuraSpecProvider(specProvider.GetSpec(forkActivation))
                : specProvider.GetSpec(forkActivation);

        public static IReleaseSpec GetSpec(this ISpecProvider specProvider, long blockNumber, ulong? timestamp) => specProvider.GetSpec(new ForkActivation(blockNumber, timestamp));
        public static IReleaseSpec GetSpec(this ISpecProvider specProvider, BlockHeader blockHeader) => specProvider.GetSpec(new ForkActivation(blockHeader.Number, blockHeader.Timestamp));

        /// <summary>
        /// Resolves a spec for all planned forks applied.
        /// </summary>
        /// <returns>A spec for all planned forks applied</returns>
        /// <remarks> The default value is long.MaxValue for block numbers and ulong.MaxValue for timestamps
        /// for every new not yet scheduled EIP. Because of that we can't use long.MaxValue and
        /// ulong.MaxValue for GetFinalSpec that is why we have long.MaxValue-1, ulong.MaxValue-1 </remarks>
        public static IReleaseSpec GetFinalSpec(this ISpecProvider specProvider) => specProvider.GetSpec(long.MaxValue - 1, ulong.MaxValue - 1);
    }
}
