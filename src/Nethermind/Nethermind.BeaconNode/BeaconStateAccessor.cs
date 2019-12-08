using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class BeaconStateAccessor
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconStateAccessor(IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            ICryptographyService cryptographyService,
            BeaconChainUtility beaconChainUtility)
        {
            _cryptographyService = cryptographyService;
            _beaconChainUtility = beaconChainUtility;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _signatureDomainOptions = signatureDomainOptions;
        }

        /// <summary>
        /// Return the indexed attestation corresponding to ``attestation``.
        /// </summary>
        public IndexedAttestation GetIndexedAttestation(BeaconState state, Attestation attestation)
        {
            var attestingIndices = GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
            var custodyBit1Indices = GetAttestingIndices(state, attestation.Data, attestation.CustodyBits);

            var isSubset = custodyBit1Indices.All(x => attestingIndices.Contains(x));
            if (!isSubset)
            {
                throw new Exception("Custody bit indices must be a subset of attesting indices");
            }

            var custodyBit0Indices = attestingIndices.Except(custodyBit1Indices);

            var sortedCustodyBit0Indices = custodyBit0Indices.OrderBy(x => x);
            var sortedCustodyBit1Indices = custodyBit1Indices.OrderBy(x => x);

            var indexedAttestation = new IndexedAttestation(sortedCustodyBit0Indices, sortedCustodyBit1Indices, attestation.Data, attestation.Signature);
            return indexedAttestation;
        }

        /// <summary>
        /// Return the sequence of active validator indices at ``epoch``.
        /// </summary>
        public IList<ValidatorIndex> GetActiveValidatorIndices(BeaconState state, Epoch epoch)
        {
            return state.Validators
                .Select((validator, index) => new { validator, index })
                .Where(x => _beaconChainUtility.IsActiveValidator(x.validator, epoch))
                .Select(x => (ValidatorIndex)(ulong)x.index)
                .ToList();
        }

        /// <summary>
        /// Return the set of attesting indices corresponding to ``data`` and ``bits``.
        /// </summary>
        public IEnumerable<ValidatorIndex> GetAttestingIndices(BeaconState state, AttestationData data, BitArray bits)
        {
            var committee = GetBeaconCommittee(state, data.Slot, data.Index);
            return committee.Where((x, index) => bits[index]);
        }

        /// <summary>
        /// Return the beacon committee at ``slot`` for ``index``.
        /// </summary>
        public IReadOnlyList<ValidatorIndex> GetBeaconCommittee(BeaconState state, Slot slot, CommitteeIndex index)
        {
            var epoch = _beaconChainUtility.ComputeEpochAtSlot(slot);
            var committeesPerSlot = GetCommitteeCountAtSlot(state, slot);
            //var committeeCount = GetCommitteeCount(state, epoch);

            var indices = GetActiveValidatorIndices(state, epoch);
            var seed = GetSeed(state, epoch, _signatureDomainOptions.CurrentValue.BeaconAttester);
            //var index = (shard + miscellaneousParameters.ShardCount - GetStartShard(state, epoch)) % miscellaneousParameters.ShardCount;
            var committeeIndex = (ulong)(slot % _timeParameterOptions.CurrentValue.SlotsPerEpoch) * committeesPerSlot + (ulong)index;
            var committeeCount = committeesPerSlot * (ulong)_timeParameterOptions.CurrentValue.SlotsPerEpoch;

            var committee = _beaconChainUtility.ComputeCommittee(indices, seed, committeeIndex, committeeCount);
            return committee;
        }

        /// <summary>
        /// Return the beacon proposer index at the current slot.
        /// </summary>
        public ValidatorIndex GetBeaconProposerIndex(BeaconState state)
        {
            var epoch = GetCurrentEpoch(state);

            var seedBytes = new Span<byte>(new byte[40]);
            var initialSeed = GetSeed(state, epoch, _signatureDomainOptions.CurrentValue.BeaconProposer);
            initialSeed.AsSpan().CopyTo(seedBytes);
            BitConverter.TryWriteBytes(seedBytes.Slice(32), (ulong)state.Slot);
            if (!BitConverter.IsLittleEndian)
            {
                seedBytes.Slice(32).Reverse();
            }
            var seed = _cryptographyService.Hash(seedBytes);

            var indices = GetActiveValidatorIndices(state, epoch);
            var proposerIndex = _beaconChainUtility.ComputeProposerIndex(state, indices, seed);
            return proposerIndex;
        }

        /// <summary>
        /// Return the validator churn limit for the current epoch.
        /// </summary>
        public ulong GetValidatorChurnLimit(BeaconState state)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            var currentEpoch = GetCurrentEpoch(state);
            var activeValidatorIndices = GetActiveValidatorIndices(state, currentEpoch);
            var churnLimit = (ulong)activeValidatorIndices.Count / miscellaneousParameters.ChurnLimitQuotient;
            return Math.Max(churnLimit, miscellaneousParameters.MinimumPerEpochChurnLimit);
        }

        /// <summary>
        /// Return the block root at the start of a recent ``epoch``.
        /// </summary>
        public Hash32 GetBlockRoot(BeaconState state, Epoch epoch)
        {
            var startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            return GetBlockRootAtSlot(state, startSlot);
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
            return state.BlockRoots[(int)blockIndex];
        }

        ///// <summary>
        ///// Return the number of committees at ``epoch``.
        ///// </summary>
        //public ulong GetCommitteeCount(BeaconState state, Epoch epoch)
        //{
        //    var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
        //    var timeParameters = _timeParameterOptions.CurrentValue;

        //    var shardsPerEpoch = (ulong)miscellaneousParameters.ShardCount / (ulong)timeParameters.SlotsPerEpoch;
        //    var activeValidators = (ulong)GetActiveValidatorIndices(state, epoch).Count;
        //    var availableValidatorCommittees = (activeValidators / (ulong)timeParameters.SlotsPerEpoch) / miscellaneousParameters.TargetCommitteeSize;

        //    var committeesPerSlot = Math.Max(1, Math.Min(shardsPerEpoch, availableValidatorCommittees));
        //    return committeesPerSlot * (ulong)timeParameters.SlotsPerEpoch;
        //}

        ///// <summary>
        ///// Return the crosslink committee at ``epoch`` for ``shard``.
        ///// </summary>
        //public IReadOnlyList<ValidatorIndex> GetCrosslinkCommittee(BeaconState state, Epoch epoch, Shard shard)
        //{
        //    var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;

        //    var indices = GetActiveValidatorIndices(state, epoch);
        //    var seed = GetSeed(state, epoch, DomainType.BeaconAttester);
        //    var index = (shard + miscellaneousParameters.ShardCount - GetStartShard(state, epoch)) % miscellaneousParameters.ShardCount;
        //    var committeeCount = GetCommitteeCount(state, epoch);
        //    var committee = _beaconChainUtility.ComputeCommittee(indices, seed, index, committeeCount);
        //    return committee;
        //}

        /// <summary>
        /// Return the number of committees at ``slot``.
        /// </summary>
        public ulong GetCommitteeCountAtSlot(BeaconState state, Slot slot)
        {
            var epoch = _beaconChainUtility.ComputeEpochAtSlot(slot);
            var indices = GetActiveValidatorIndices(state, epoch);
            var committeeCount = (ulong)indices.Count
                / (ulong)_timeParameterOptions.CurrentValue.SlotsPerEpoch
                / _miscellaneousParameterOptions.CurrentValue.TargetCommitteeSize;

            return Math.Max(1, Math.Min(_miscellaneousParameterOptions.CurrentValue.MaximumCommitteesPerSlot, committeeCount));
        }

        /// <summary>
        /// Return the current epoch.
        /// </summary>
        public Epoch GetCurrentEpoch(BeaconState state)
        {
            return _beaconChainUtility.ComputeEpochAtSlot(state.Slot);
        }

        /// <summary>
        /// Return the signature domain (fork version concatenated with domain type) of a message.
        /// </summary>
        public Domain GetDomain(BeaconState state, DomainType domainType, Epoch messageEpoch)
        {
            Epoch epoch;
            if (messageEpoch == Epoch.None)
            {
                epoch = GetCurrentEpoch(state);
            }
            else
            {
                epoch = messageEpoch;
            }

            ForkVersion forkVersion;
            if (epoch < state.Fork.Epoch)
            {
                forkVersion = state.Fork.PreviousVersion;
            }
            else
            {
                forkVersion = state.Fork.CurrentVersion;
            }

            return _beaconChainUtility.ComputeDomain(domainType, forkVersion);
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
            var index = (int)(epoch % _stateListLengthOptions.CurrentValue.EpochsPerHistoricalVector);
            var mix = state.RandaoMixes[index];
            return mix;
        }

        /// <summary>
        /// Return the seed at ``epoch``.
        /// </summary>
        public Hash32 GetSeed(BeaconState state, Epoch epoch, DomainType domainType)
        {
            Epoch mixEpoch = (Epoch)(epoch + _stateListLengthOptions.CurrentValue.EpochsPerHistoricalVector
                - _timeParameterOptions.CurrentValue.MinimumSeedLookahead - 1UL);
            // # Avoid underflow
            var mix = GetRandaoMix(state, mixEpoch);
            var seedHashInput = new Span<byte>(new byte[4 + 8 + 32]);
            BinaryPrimitives.WriteUInt32LittleEndian(seedHashInput, (uint)domainType);
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

        ///// <summary>
        ///// Return the number of shards to increment ``state.start_shard`` at ``epoch``.
        ///// </summary>
        //public Shard GetShardDelta(BeaconState state, Epoch epoch)
        //{
        //    var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
        //    var committeeCount = GetCommitteeCount(state, epoch);
        //    var maxShard = miscellaneousParameters.ShardCount - (miscellaneousParameters.ShardCount / (ulong)_timeParameterOptions.CurrentValue.SlotsPerEpoch);
        //    return Shard.Min(new Shard(committeeCount), maxShard);
        //}

        ///// <summary>
        ///// Return the start shard of the 0th committee at ``epoch``.
        ///// </summary>
        //public Shard GetStartShard(BeaconState state, Epoch epoch)
        //{
        //    var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
        //    var currentEpoch = GetCurrentEpoch(state);
        //    var oneEpoch = new Epoch(1);
        //    var checkEpoch = currentEpoch + oneEpoch;
        //    if (epoch > checkEpoch)
        //    {
        //        throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "Epoch is too far in the future");
        //    }

        //    var initialShardDelta = GetShardDelta(state, currentEpoch);
        //    throw new NotImplementedException();
        //    //var shard = (state.StartShard + initialShardDelta) % miscellaneousParameters.ShardCount;

        //    //while (checkEpoch > epoch)
        //    //{
        //    //    checkEpoch -= oneEpoch;
        //    //    var shardDelta = GetShardDelta(state, checkEpoch);
        //    //    shard = (shard + miscellaneousParameters.ShardCount + shardDelta) % miscellaneousParameters.ShardCount;
        //    //}

        //    //return shard;
        //}

        /// <summary>
        /// Return the combined effective balance of the active validators
        /// </summary>
        public Gwei GetTotalActiveBalance(BeaconState state)
        {
            var epoch = GetPreviousEpoch(state);
            var validatorIndices = GetActiveValidatorIndices(state, epoch);
            return GetTotalBalance(state, validatorIndices);
        }

        /// <summary>
        /// Return the combined effective balance of the ``indices``. (1 Gwei minimum to avoid divisions by zero.)
        /// </summary>
        public Gwei GetTotalBalance(BeaconState state, IEnumerable<ValidatorIndex> validatorIndices)
        {
            var total = Gwei.Zero;
            foreach (var index in validatorIndices)
            {
                var validator = state.Validators[(int)index];
                var balance = validator.EffectiveBalance;
                total += balance;
            }
            if (total == Gwei.Zero)
            {
                return new Gwei(1);
            }
            return total;
        }
    }
}
