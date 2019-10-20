using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode
{
    public class BeaconStateAccessor
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconStateAccessor(IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            ICryptographyService cryptographyService,
            BeaconChainUtility beaconChainUtility)
        {
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
        }

        /// <summary>
        /// Return the set of attesting indices corresponding to ``data`` and ``bits``.
        /// </summary>
        public IEnumerable<ValidatorIndex> GetAttestingIndices(BeaconState state, AttestationData data, BitArray bits)
        {
            var committee = GetCrosslinkCommittee(state, data.Target.Epoch, data.Crosslink.Shard);
            return committee.Where((x, index) => bits[index]);
        }

        /// <summary>
        /// Return the block root at a recent ``slot``.
        /// </summary>
        public Hash32 GetBlockRootAtSlot(BeaconState state, Slot slot)
        {
            var timeParameters = _timeParameterOptions.CurrentValue;
            // NOTE: Need to use '+' to avoid underflow issues
            if (slot + timeParameters.SlotsPerHistoricalRoot < state.Slot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot can not be more than one root ({timeParameters.SlotsPerHistoricalRoot} slots) behind the state slot {state.Slot}");
            }
            if (slot >= state.Slot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot must be less than than the state slot {state.Slot}");
            }
            var blockIndex = slot % timeParameters.SlotsPerHistoricalRoot;
            return state.BlockRoots[(int)(ulong)blockIndex];
        }

        /// <summary>
        /// Return the number of committees at ``epoch``.
        /// </summary>
        public ulong GetCommitteeCount(BeaconState state, Epoch epoch)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            var timeParameters = _timeParameterOptions.CurrentValue;

            var shardsPerEpoch = (ulong)miscellaneousParameters.ShardCount / (ulong)timeParameters.SlotsPerEpoch;
            var activeValidators = (ulong)state.GetActiveValidatorIndices(epoch).Count;
            var availableValidatorCommittees = (activeValidators / (ulong)timeParameters.SlotsPerEpoch) / miscellaneousParameters.TargetCommitteeSize;

            var committeesPerSlot = Math.Max(1, Math.Min(shardsPerEpoch, availableValidatorCommittees));
            return committeesPerSlot * (ulong)timeParameters.SlotsPerEpoch;
        }

        /// <summary>
        /// Return the crosslink committee at ``epoch`` for ``shard``.
        /// </summary>
        public IReadOnlyList<ValidatorIndex> GetCrosslinkCommittee(BeaconState state, Epoch epoch, Shard shard)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;

            var indices = state.GetActiveValidatorIndices(epoch);
            var seed = GetSeed(state, epoch, DomainType.BeaconAttester);
            var index = (shard + miscellaneousParameters.ShardCount - GetStartShard(state, epoch)) % miscellaneousParameters.ShardCount;
            var committeeCount = GetCommitteeCount(state, epoch);
            var committee = _beaconChainUtility.ComputeCommittee(indices, seed, index, committeeCount);
            return committee;
        }

        /// <summary>
        /// Return the current epoch.
        /// </summary>
        public Epoch GetCurrentEpoch(BeaconState state)
        {
            return _beaconChainUtility.ComputeEpochOfSlot(state.Slot);
        }

        /// <summary>
        /// Return the previous epoch (unless the current epoch is ``GENESIS_EPOCH``).
        /// </summary>
        public Epoch GetPreviousEpoch(BeaconState state)
        {
            var currentEpoch = GetCurrentEpoch(state);
            if (currentEpoch == _initialValueOptions.CurrentValue.GenesisEpoch)
            {
                return _initialValueOptions.CurrentValue.GenesisEpoch;
            }
            return currentEpoch - new Epoch(1);
        }

        /// <summary>
        /// Return the randao mix at a recent ``epoch``
        /// </summary>
        public Hash32 GetRandaoMix(BeaconState state, Epoch epoch)
        {
            var index = (int)(ulong)(epoch % _stateListLengthOptions.CurrentValue.EpochsPerHistoricalVector);
            var mix = state.RandaoMixes[index];
            return mix;
        }

        /// <summary>
        /// Return the seed at ``epoch``.
        /// </summary>
        public Hash32 GetSeed(BeaconState state, Epoch epoch, DomainType domainType)
        {
            var mixEpoch = epoch + _stateListLengthOptions.CurrentValue.EpochsPerHistoricalVector
                - _timeParameterOptions.CurrentValue.MinimumSeedLookahead - new Epoch(1);
            // # Avoid underflow
            var mix = GetRandaoMix(state, mixEpoch);
            var seedHashInput = new Span<byte>(new byte[4 + 8 + 32]);
            domainType.AsSpan().CopyTo(seedHashInput);
            var epochBytes = BitConverter.GetBytes((ulong)epoch);
            if (!BitConverter.IsLittleEndian)
            {
                epochBytes = epochBytes.Reverse().ToArray();
            }
            epochBytes.CopyTo(seedHashInput.Slice(4));
            mix.AsSpan().CopyTo(seedHashInput.Slice(12));
            var seed = _cryptographyService.Hash(seedHashInput);
            return seed;
        }

        /// <summary>
        /// Return the number of shards to increment ``state.start_shard`` at ``epoch``.
        /// </summary>
        public Shard GetShardDelta(BeaconState state, Epoch epoch)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            var committeeCount = GetCommitteeCount(state, epoch);
            var maxShard = miscellaneousParameters.ShardCount - (miscellaneousParameters.ShardCount / (ulong)_timeParameterOptions.CurrentValue.SlotsPerEpoch);
            return Shard.Min(new Shard(committeeCount), maxShard);
        }

        /// <summary>
        /// Return the start shard of the 0th committee at ``epoch``.
        /// </summary>
        public Shard GetStartShard(BeaconState state, Epoch epoch)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            var currentEpoch = GetCurrentEpoch(state);
            var oneEpoch = new Epoch(1);
            var checkEpoch = currentEpoch + oneEpoch;
            if (epoch > checkEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "Epoch is too far in the future");
            }

            var initialShardDelta = GetShardDelta(state, currentEpoch);
            var shard = (state.StartShard + initialShardDelta) % miscellaneousParameters.ShardCount;

            while (checkEpoch > epoch)
            {
                checkEpoch -= oneEpoch;
                var shardDelta = GetShardDelta(state, checkEpoch);
                shard = (shard + miscellaneousParameters.ShardCount + shardDelta) % miscellaneousParameters.ShardCount;
            }

            return shard;
        }

        /// <summary>
        /// Return the combined effective balance of the active validators
        /// </summary>
        public Gwei GetTotalActiveBalance(BeaconState state)
        {
            var epoch = GetPreviousEpoch(state);
            var validatorIndices = state.GetActiveValidatorIndices(epoch);
            return GetTotalBalance(state, validatorIndices);
        }

        /// <summary>
        /// Return the combined effective balance of the ``indices``. (1 Gwei minimum to avoid divisions by zero.)
        /// </summary>
        public Gwei GetTotalBalance(BeaconState state, IEnumerable<ValidatorIndex> validatorIndices)
        {
            var total = new Gwei(0);
            foreach (var index in validatorIndices)
            {
                var validator = state.Validators[(int)(ulong)index];
                var balance = validator.EffectiveBalance;
                total += balance;
            }
            if (total == new Gwei(0))
            {
                return new Gwei(1);
            }
            return total;
        }

        /// <summary>
        /// Return the block root at the start of a recent ``epoch``.
        /// </summary>
        internal Hash32 GetBlockRoot(BeaconState state, Epoch epoch)
        {
            var startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            return GetBlockRootAtSlot(state, startSlot);
        }
    }
}
