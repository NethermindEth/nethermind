//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class BeaconChainUtility : IBeaconChainUtility
    {
        private readonly ChainConstants _chainConstants;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<GweiValues> _gweiValueOptions;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconChainUtility(ILogger<BeaconChainUtility> logger,
            ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            ICryptographyService cryptographyService)
        {
            _cryptographyService = cryptographyService;
            _logger = logger;
            _chainConstants = chainConstants;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _initialValueOptions = initialValueOptions;
            _gweiValueOptions = gweiValueOptions;
            _timeParameterOptions = timeParameterOptions;
        }

        /// <summary>
        /// Return the epoch during which validator activations and exits initiated in ``epoch`` take effect.
        /// </summary>
        public Epoch ComputeActivationExitEpoch(Epoch epoch)
        {
            return (Epoch) (epoch + 1UL + _timeParameterOptions.CurrentValue.MaximumSeedLookahead);
        }

        /// <summary>
        /// Return the committee corresponding to ``indices``, ``seed``, ``index``, and committee ``count``.
        /// </summary>
        public IReadOnlyList<ValidatorIndex> ComputeCommittee(IList<ValidatorIndex> indices, Bytes32 seed, ulong index,
            ulong count)
        {
            ulong start = (ulong) indices.Count * index / count;
            ulong end = (ulong) indices.Count * (index + 1) / count;
            List<ValidatorIndex> shuffled = new List<ValidatorIndex>();
            for (ulong i = start; i < end; i++)
            {
                ValidatorIndex shuffledLookup =
                    ComputeShuffledIndex(new ValidatorIndex(i), (ulong) indices.Count, seed);
                ValidatorIndex shuffledIndex = indices[(int) (ulong) shuffledLookup];
                shuffled.Add(shuffledIndex);
            }

            return shuffled;
        }

        /// <summary>
        /// Returns the domain for the 'domain_type' and 'fork_version'
        /// </summary>
        public Domain ComputeDomain(DomainType domainType, ForkVersion? forkVersion = null)
        {
            forkVersion ??= _initialValueOptions.CurrentValue.GenesisForkVersion;

            Span<byte> combined = stackalloc byte[Domain.Length];
            domainType.AsSpan().CopyTo(combined);
            forkVersion.Value.AsSpan().CopyTo(combined.Slice(DomainType.Length));
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
        public ValidatorIndex ComputeProposerIndex(BeaconState state, IList<ValidatorIndex> indices, Bytes32 seed)
        {
            if (!indices.Any())
            {
                throw new ArgumentException("Indices can not be empty", nameof(indices));
            }

            ulong indexCount = (ulong) indices.Count;
            ValidatorIndex index = 0UL;
            Span<byte> randomInputBytes = stackalloc byte[40];
            seed.AsSpan().CopyTo(randomInputBytes);
            while (true)
            {
                ValidatorIndex initialValidatorIndex = (ValidatorIndex) (index % indexCount);
                ValidatorIndex shuffledIndex = ComputeShuffledIndex(initialValidatorIndex, indexCount, seed);
                ValidatorIndex candidateIndex = indices[(int) shuffledIndex];

                BinaryPrimitives.WriteUInt64LittleEndian(randomInputBytes.Slice(32), index / 32);
                Bytes32 randomHash = _cryptographyService.Hash(randomInputBytes);
                byte random = randomHash.AsSpan()[(int) (index % 32)];

                Gwei effectiveBalance = state.Validators[(int) candidateIndex].EffectiveBalance;
                if ((effectiveBalance * byte.MaxValue) >=
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
        public ValidatorIndex ComputeShuffledIndex(ValidatorIndex index, ulong indexCount, Bytes32 seed)
        {
            if (index >= indexCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    $"Index should be less than indexCount {indexCount}");
            }

            // Swap or not (https://link.springer.com/content/pdf/10.1007%2F978-3-642-32009-5_1.pdf)
            // See the 'generalized domain' algorithm on page 3

            Span<byte> pivotHashInput = stackalloc byte[33];
            seed.AsSpan().CopyTo(pivotHashInput);
            Span<byte> sourceHashInput = stackalloc byte[37];
            seed.AsSpan().CopyTo(sourceHashInput);
            for (int currentRound = 0;
                currentRound < _miscellaneousParameterOptions.CurrentValue.ShuffleRoundCount;
                currentRound++)
            {
                byte roundByte = (byte) (currentRound & 0xFF);
                pivotHashInput[32] = roundByte;
                Bytes32 pivotHash = _cryptographyService.Hash(pivotHashInput);
                ReadOnlySpan<byte> pivotBytes = pivotHash.AsSpan().Slice(0, 8);
                ValidatorIndex pivot = BinaryPrimitives.ReadUInt64LittleEndian(pivotBytes) % indexCount;

                ValidatorIndex flip = (pivot + indexCount - index) % indexCount;

                ValidatorIndex position = ValidatorIndex.Max(index, flip);

                sourceHashInput[32] = roundByte;
                BinaryPrimitives.WriteUInt32LittleEndian(sourceHashInput.Slice(33), (uint) position / 256);
                Bytes32 source = _cryptographyService.Hash(sourceHashInput.ToArray());

                byte flipByte = source.AsSpan()[(int) ((position % 256) / 8)];

                int flipBit = (flipByte >> (int) (position % 8)) % 2;

                if (flipBit == 1)
                {
                    index = flip;
                }
            }

            return index;
        }

        /// <summary>
        /// Return the signing root of an object by calculating the root of the object-domain tree.
        /// </summary>
        public Root ComputeSigningRoot(Root objectRoot, Domain domain)
        {
            SigningRoot domainWrappedObject = new SigningRoot(objectRoot, domain);
            return _cryptographyService.HashTreeRoot(domainWrappedObject);
        }

        /// <summary>
        /// Return the start slot of 'epoch'
        /// </summary>
        public Slot ComputeStartSlotOfEpoch(Epoch epoch)
        {
            return (Slot) (_timeParameterOptions.CurrentValue.SlotsPerEpoch * epoch.Number);
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
        /// Check if ``validator`` is eligible for activation.
        /// </summary>
        public bool IsEligibleForActivation(BeaconState state, Validator validator)
        {
            // Placement in queue is finalized            
            return validator.ActivationEligibilityEpoch <= state.FinalizedCheckpoint.Epoch
                   // Has not yet been activated
                   && validator.ActivationEpoch == _chainConstants.FarFutureEpoch;
        }

        /// <summary>
        /// Check if ``validator`` is eligible to be placed into the activation queue.
        /// </summary>
        public bool IsEligibleForActivationQueue(Validator validator)
        {
            return validator.ActivationEligibilityEpoch == _chainConstants.FarFutureEpoch
                   && validator.EffectiveBalance == _gweiValueOptions.CurrentValue.MaximumEffectiveBalance;
        }

        /// <summary>
        /// Check if ``data_1`` and ``data_2`` are slashable according to Casper FFG rules.
        /// </summary>
        public bool IsSlashableAttestationData(AttestationData data1, AttestationData data2)
        {
            bool isDoubleVote = data1.Target.Epoch == data2.Target.Epoch && !data1.Equals(data2);
            bool isSurroundVote = data1.Source.Epoch < data2.Source.Epoch && data2.Target.Epoch < data1.Target.Epoch;
            bool isSlashable = isDoubleVote || isSurroundVote;
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
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            IReadOnlyList<ValidatorIndex> attestingIndices = indexedAttestation.AttestingIndices;

            // Verify max number of indices
            if ((ulong) attestingIndices.Count > miscellaneousParameters.MaximumValidatorsPerCommittee)
            {
                if (_logger.IsWarn())
                    Log.InvalidIndexedAttestationTooMany(_logger, indexedAttestation.Data.Index,
                        indexedAttestation.Data.Slot, attestingIndices.Count,
                        miscellaneousParameters.MaximumValidatorsPerCommittee, null);
                return false;
            }

            // Verify indices are sorted and unique
            if (attestingIndices.Count() > 1)
            {
                for (int index = 0; index < attestingIndices.Count() - 1; index++)
                {
                    if (!(attestingIndices[index] < attestingIndices[index + 1]))
                    {
                        if (attestingIndices[index] == attestingIndices[index + 1])
                        {
                            if (_logger.IsWarn())
                                Log.InvalidIndexedAttestationNotUnique(_logger, indexedAttestation.Data.Index,
                                    indexedAttestation.Data.Slot, 0, index, null);
                        }
                        else
                        {
                            if (_logger.IsWarn())
                                Log.InvalidIndexedAttestationNotSorted(_logger, indexedAttestation.Data.Index,
                                    indexedAttestation.Data.Slot, 0, index, null);
                        }

                        return false;
                    }
                }
            }

            // TODO: BLS FastAggregateVerify (see spec)

            // Verify aggregate signature
            IList<BlsPublicKey> publicKeys = attestingIndices.Select(x => state.Validators[(int) (ulong) x].PublicKey)
                .ToList();

            Root attestationDataRoot = _cryptographyService.HashTreeRoot(indexedAttestation.Data);
            Root signingRoot = ComputeSigningRoot(attestationDataRoot, domain);

            BlsSignature signature = indexedAttestation.Signature;

            //BlsPublicKey aggregatePublicKey = _cryptographyService.BlsAggregatePublicKeys(publicKeys);
            //bool isValid = _cryptographyService.BlsVerify(aggregatePublicKey, signingRoot, signature);

            bool isValid = _cryptographyService.BlsFastAggregateVerify(publicKeys, signingRoot, signature);

            if (!isValid)
            {
                if (_logger.IsWarn())
                    Log.InvalidIndexedAttestationSignature(_logger, indexedAttestation.Data.Index,
                        indexedAttestation.Data.Slot, null);
                return false;
            }

            return true;
        }
    }
}