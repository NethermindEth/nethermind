using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Crypto;

namespace Nethermind.BeaconNode
{
    public class BeaconChainUtility
    {
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<GweiValues> _gweiValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconChainUtility(ILogger<BeaconChainUtility> logger,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            ICryptographyService cryptographyService)
        {
            _cryptographyService = cryptographyService;
            _logger = logger;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _gweiValueOptions = gweiValueOptions;
            _timeParameterOptions = timeParameterOptions;
        }

        /// <summary>
        /// Return the epoch during which validator activations and exits initiated in ``epoch`` take effect.
        /// </summary>
        public Epoch ComputeActivationExitEpoch(Epoch epoch)
        {
            return epoch + new Epoch(1) + _timeParameterOptions.CurrentValue.MaximumSeedLookahead;
        }

        /// <summary>
        /// Return the committee corresponding to ``indices``, ``seed``, ``index``, and committee ``count``.
        /// </summary>
        public IReadOnlyList<ValidatorIndex> ComputeCommittee(IList<ValidatorIndex> indices, Hash32 seed, ulong index, ulong count)
        {
            var start = ((ulong)indices.Count * (ulong)index) / count;
            var end = ((ulong)indices.Count * ((ulong)index + 1)) / count;
            var shuffled = new List<ValidatorIndex>();
            for (var i = start; i < end; i++)
            {
                var shuffledLookup = ComputeShuffledIndex(new ValidatorIndex(i), (ulong)indices.Count, seed);
                var shuffledIndex = indices[(int)(ulong)shuffledLookup];
                shuffled.Add(shuffledIndex);
            }
            return shuffled;
        }

        /// <summary>
        /// Returns the domain for the 'domain_type' and 'fork_version'
        /// </summary>
        public Domain ComputeDomain(DomainType domainType, ForkVersion forkVersion = new ForkVersion())
        {
            var combined = new Span<byte>(new byte[Domain.Length]);
            domainType.AsSpan().CopyTo(combined);
            forkVersion.AsSpan().CopyTo(combined.Slice(DomainType.Length));
            return new Domain(combined);
        }

        /// <summary>
        /// Return the epoch number of ``slot``.
        /// </summary>
        public Epoch ComputeEpochAtSlot(Slot slot)
        {
            return new Epoch(slot / _timeParameterOptions.CurrentValue.SlotsPerEpoch);
        }

        /// <summary>
        /// Return from ``indices`` a random index sampled by effective balance.
        /// </summary>
        public ValidatorIndex ComputeProposerIndex(BeaconState state, IList<ValidatorIndex> indices, Hash32 seed)
        {
            if (!indices.Any())
            {
                throw new ArgumentException("Indices can not be empty", nameof(indices));
            }

            const ulong maxRandomByte = (1 << 8) - 1;
            var indexCount = (ulong)indices.Count();
            var index = (ulong)0;
            while (true)
            {
                var initialValidatorIndex = new ValidatorIndex(index % indexCount);
                var shuffledIndex = ComputeShuffledIndex(initialValidatorIndex, indexCount, seed);
                var candidateIndex = indices[(int)(ulong)shuffledIndex];

                var randomInputBytes = new Span<byte>(new byte[40]);
                seed.AsSpan().CopyTo(randomInputBytes);
                BitConverter.TryWriteBytes(randomInputBytes.Slice(32), index / 32);
                if (!BitConverter.IsLittleEndian)
                {
                    randomInputBytes.Slice(32).Reverse();
                }
                var randomHash = _cryptographyService.Hash(randomInputBytes);
                var random = randomHash.AsSpan()[(int)(index % 32)];

                var effectiveBalance = state.Validators[(int)(ulong)candidateIndex].EffectiveBalance;
                if ((effectiveBalance * maxRandomByte) >=
                    (_gweiValueOptions.CurrentValue.MaximumEffectiveBalance * random))
                {
                    return candidateIndex;
                }
                index++;
            }
        }

        /// <summary>
        /// Return the shuffled validator index corresponding to ``seed`` (and ``index_count``).
        /// </summary>
        public ValidatorIndex ComputeShuffledIndex(ValidatorIndex index, ulong indexCount, Hash32 seed)
        {
            if ((ulong)index >= indexCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index should be less than indexCount {indexCount}");
            }

            // Swap or not (https://link.springer.com/content/pdf/10.1007%2F978-3-642-32009-5_1.pdf)
            // See the 'generalized domain' algorithm on page 3

            var pivotHashInput = new Span<byte>(new byte[33]);
            seed.AsSpan().CopyTo(pivotHashInput);
            var sourceHashInput = new Span<byte>(new byte[37]);
            seed.AsSpan().CopyTo(sourceHashInput);
            for (var currentRound = 0; currentRound < _miscellaneousParameterOptions.CurrentValue.ShuffleRoundCount; currentRound++)
            {
                var roundByte = (byte)(currentRound & 0xFF);
                pivotHashInput[32] = roundByte;
                var pivotHash = _cryptographyService.Hash(pivotHashInput);
                var pivotBytes = pivotHash.AsSpan().Slice(0, 8).ToArray();
                if (!BitConverter.IsLittleEndian)
                {
                    pivotBytes = pivotBytes.Reverse().ToArray();
                }
                var pivot = BitConverter.ToUInt64(pivotBytes.ToArray()) % indexCount;

                var flip = new ValidatorIndex((pivot + indexCount - (ulong)index) % indexCount);

                var position = ValidatorIndex.Max(index, flip);

                sourceHashInput[32] = roundByte;
                var positionBytes = BitConverter.GetBytes((uint)(ulong)position / 256);
                if (!BitConverter.IsLittleEndian)
                {
                    positionBytes = positionBytes.Reverse().ToArray();
                }
                positionBytes.CopyTo(sourceHashInput.Slice(33));
                var source = _cryptographyService.Hash(sourceHashInput.ToArray());

                var flipByte = source.AsSpan().Slice((int)(((uint)(ulong)position % 256) / 8), 1).ToArray()[0];

                var flipBit = (flipByte >> (int)((ulong)position % 8)) % 2;

                if (flipBit == 1)
                {
                    index = flip;
                }
            }

            return index;
        }

        /// <summary>
        /// Return the start slot of 'epoch'
        /// </summary>
        public Slot ComputeStartSlotOfEpoch(Epoch epoch)
        {
            return _timeParameterOptions.CurrentValue.SlotsPerEpoch * (ulong)epoch;
        }

        /// <summary>
        /// Return the largest integer ``x`` such that ``x**2 <= n``.
        /// </summary>
        public ulong IntegerSquareRoot(ulong value)
        {
            var x = value;
            var y = (x + 1) / 2;
            while (y < x)
            {
                x = y;
                y = (x + (value / x)) / 2;
            }
            return x;
        }

        /// <summary>
        /// Check if ``validator`` is active.
        /// </summary>
        public bool IsActiveValidator(Validator validator, Epoch epoch)
        {
            return validator.ActivationEpoch <= epoch
                && epoch < validator.ExitEpoch;
        }

        /// <summary>
        /// Check if ``data_1`` and ``data_2`` are slashable according to Casper FFG rules.
        /// </summary>
        public bool IsSlashableAttestationData(AttestationData data1, AttestationData data2)
        {
            var isSlashable =
                // Double vote
                (data1.Target.Epoch == data2.Target.Epoch && !data1.Equals(data2))
                // Surround vote
                || (data1.Source.Epoch < data2.Source.Epoch && data2.Target.Epoch < data1.Target.Epoch);
            return isSlashable;
        }

        /// <summary>
        /// Check if ``validator`` is slashable.
        /// </summary>
        public bool IsSlashableValidator(Validator validator, Epoch epoch)
        {
            return (!validator.IsSlashed)
                && (validator.ActivationEpoch <= epoch)
                && (epoch < validator.WithdrawableEpoch);
        }

        /// <summary>
        /// Check if ``indexed_attestation`` has valid indices and signature.
        /// </summary>
        public bool IsValidIndexedAttestation(BeaconState state, IndexedAttestation indexedAttestation, Domain domain)
        {
            var miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            var bit0Indices = indexedAttestation.CustodyBit0Indices;
            var bit1Indices = indexedAttestation.CustodyBit1Indices;

            // Verify no index has custody bit equal to 1 [to be removed in phase 1]
            if (bit1Indices.Count() != 0) // [to be removed in phase 1]
            {
                _logger.LogWarning(Event.InvalidIndexedAttestation,
                    "Invalid indexed attestion from committee {CommitteeIndex} for slot {Slot}, because it has {BitIndicesCount} bit 1 indices.",
                    indexedAttestation.Data.Index, indexedAttestation.Data.Slot, bit1Indices.Count());
                return false; //[to be removed in phase 1]
            }

            // Verify max number of indices
            var totalIndices = bit0Indices.Count() + bit1Indices.Count();
            if ((ulong)totalIndices > miscellaneousParameters.MaximumValidatorsPerCommittee)
            {
                _logger.LogWarning(Event.InvalidIndexedAttestation,
                    "Invalid indexed attestion from committee {CommitteeIndex} for slot {Slot}, because it has total indices {TotalIndices}, more than the maximum validators per committe {MaximumValidatorsPerCommittee}.",
                    indexedAttestation.Data.Index, indexedAttestation.Data.Slot, totalIndices, miscellaneousParameters.MaximumValidatorsPerCommittee);
                return false;
            }

            // Verify index sets are disjoint
            var intersect = bit0Indices.Intersect(bit1Indices);
            if (intersect.Count() != 0)
            {
                _logger.LogWarning(Event.InvalidIndexedAttestation,
                    "Invalid indexed attestion from committee {CommitteeIndex} for slot {Slot}, because it has {IntersectingValidatorCount} validator indexes in common between custody bit 0 and custody bit 1.",
                    indexedAttestation.Data.Index, indexedAttestation.Data.Slot, intersect.Count());
                return false;
            }

            // Verify indices are sorted
            if (bit0Indices.Count() > 1)
            {
                for (var index = 0; index < bit0Indices.Count() - 1; index++)
                {
                    if (!(bit0Indices[index] < bit0Indices[index + 1]))
                    {
                        _logger.LogWarning(Event.InvalidIndexedAttestation,
                            "Invalid indexed attestion from committee {CommitteeIndex} for slot {Slot}, because custody bit 0 index {IndexNumber} is not sorted.",
                            indexedAttestation.Data.Index, indexedAttestation.Data.Slot, index);
                        return false;
                    }
                }
            }
            if (bit1Indices.Count() > 1)
            {
                for (var index = 0; index < bit1Indices.Count() - 1; index++)
                {
                    if (!(bit1Indices[index] < bit1Indices[index + 1]))
                    {
                        _logger.LogWarning(Event.InvalidIndexedAttestation,
                            "Invalid indexed attestion from committee {CommitteeIndex} for slot {Slot}, because custody bit 1 index {IndexNumber} is not sorted.",
                            indexedAttestation.Data.Index, indexedAttestation.Data.Slot, index);
                        return false;
                    }
                }
            }

            // Verify aggregate signature
            var bit0PublicKeys = bit0Indices.Select(x => state.Validators[(int)(ulong)x].PublicKey);
            var bit0AggregatePublicKey = _cryptographyService.BlsAggregatePublicKeys(bit0PublicKeys);
            var bit1PublicKeys = bit1Indices.Select(x => state.Validators[(int)(ulong)x].PublicKey);
            var bit1AggregatePublicKey = _cryptographyService.BlsAggregatePublicKeys(bit1PublicKeys);
            var publicKeys = new BlsPublicKey[] { bit0AggregatePublicKey, bit1AggregatePublicKey };

            var attestationDataAndCustodyBit0 = new AttestationDataAndCustodyBit(indexedAttestation.Data, false);
            var messageHashBit0 = attestationDataAndCustodyBit0.HashTreeRoot();
            var attestationDataAndCustodyBit1 = new AttestationDataAndCustodyBit(indexedAttestation.Data, true);
            var messageHashBit1 = attestationDataAndCustodyBit1.HashTreeRoot();
            var messageHashes = new Hash32[] { messageHashBit0, messageHashBit1 };

            var signature = indexedAttestation.Signature;

            var isValid = _cryptographyService.BlsVerifyMultiple(publicKeys, messageHashes, signature, domain);
            if (!isValid)
            {
                _logger.LogWarning(Event.InvalidIndexedAttestation,
                    "Invalid indexed attestion from committee {CommitteeIndex} for slot {Slot}, because the aggregate signature does not match.",
                    indexedAttestation.Data.Index, indexedAttestation.Data.Slot);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if 'leaf' at 'index' verifies against the Merkle 'root' and 'branch'
        /// </summary>
        public bool IsValidMerkleBranch(Hash32 leaf, IReadOnlyList<Hash32> branch, int depth, ulong index, Hash32 root)
        {
            var value = leaf;
            for (var testDepth = 0; testDepth < depth; testDepth++)
            {
                var branchValue = branch[testDepth];
                var indexAtDepth = index / ((ulong)1 << testDepth);
                if (indexAtDepth % 2 == 0)
                {
                    // Branch on right
                    value = _cryptographyService.Hash(value, branchValue);
                }
                else
                {
                    // Branch on left
                    value = _cryptographyService.Hash(branchValue, value);
                }
            }
            return value.Equals(root);
        }
    }
}
