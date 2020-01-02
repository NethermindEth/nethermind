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
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

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
            return (Epoch)(epoch + 1UL + _timeParameterOptions.CurrentValue.MaximumSeedLookahead);
        }

        /// <summary>
        /// Return the committee corresponding to ``indices``, ``seed``, ``index``, and committee ``count``.
        /// </summary>
        public IReadOnlyList<ValidatorIndex> ComputeCommittee(IList<ValidatorIndex> indices, Hash32 seed, ulong index, ulong count)
        {
            ulong start = (ulong) indices.Count * index / count;
            ulong end = (ulong) indices.Count * (index + 1) / count;
            List<ValidatorIndex> shuffled = new List<ValidatorIndex>();
            for (ulong i = start; i < end; i++)
            {
                ValidatorIndex shuffledLookup = ComputeShuffledIndex(new ValidatorIndex(i), (ulong) indices.Count, seed);
                ValidatorIndex shuffledIndex = indices[(int) (ulong) shuffledLookup];
                shuffled.Add(shuffledIndex);
            }

            return shuffled;
        }

        /// <summary>
        /// Returns the domain for the 'domain_type' and 'fork_version'
        /// </summary>
        public Domain ComputeDomain(DomainType domainType, ForkVersion forkVersion = new ForkVersion())
        {
            Span<byte> combined = stackalloc byte[Domain.Length];
            domainType.AsSpan().CopyTo(combined);
            forkVersion.AsSpan().CopyTo(combined.Slice(DomainType.SszLength));
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

            ulong indexCount = (ulong) indices.Count;
            ValidatorIndex index = 0UL;
            Span<byte> randomInputBytes = stackalloc byte[40];
            seed.AsSpan().CopyTo(randomInputBytes);
            while (true)
            {
                ValidatorIndex initialValidatorIndex = (ValidatorIndex)(index % indexCount);
                ValidatorIndex shuffledIndex = ComputeShuffledIndex(initialValidatorIndex, indexCount, seed);
                ValidatorIndex candidateIndex = indices[(int) shuffledIndex];

                BinaryPrimitives.WriteUInt64LittleEndian(randomInputBytes.Slice(32), index / 32);
                Hash32 randomHash = _cryptographyService.Hash(randomInputBytes);
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
        public ValidatorIndex ComputeShuffledIndex(ValidatorIndex index, ulong indexCount, Hash32 seed)
        {
            if (index >= indexCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index should be less than indexCount {indexCount}");
            }

            // Swap or not (https://link.springer.com/content/pdf/10.1007%2F978-3-642-32009-5_1.pdf)
            // See the 'generalized domain' algorithm on page 3

            Span<byte> pivotHashInput = stackalloc byte[33];
            seed.AsSpan().CopyTo(pivotHashInput);
            Span<byte> sourceHashInput = stackalloc byte[37];
            seed.AsSpan().CopyTo(sourceHashInput);
            for (int currentRound = 0; currentRound < _miscellaneousParameterOptions.CurrentValue.ShuffleRoundCount; currentRound++)
            {
                byte roundByte = (byte) (currentRound & 0xFF);
                pivotHashInput[32] = roundByte;
                Hash32 pivotHash = _cryptographyService.Hash(pivotHashInput);
                Span<byte> pivotBytes = pivotHash.AsSpan().Slice(0, 8);
                ValidatorIndex pivot = BinaryPrimitives.ReadUInt64LittleEndian(pivotBytes) % indexCount;

                ValidatorIndex flip = (pivot + indexCount - index) % indexCount;

                ValidatorIndex position = ValidatorIndex.Max(index, flip);

                sourceHashInput[32] = roundByte;
                BinaryPrimitives.WriteUInt32LittleEndian(sourceHashInput.Slice(33), (uint)position / 256);
                Hash32 source = _cryptographyService.Hash(sourceHashInput.ToArray());

                byte flipByte = source.AsSpan()[(int)((position % 256) / 8)];

                int flipBit = (flipByte >> (int) (position % 8)) % 2;

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
            return (Slot)(_timeParameterOptions.CurrentValue.SlotsPerEpoch * epoch.Number);
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
            bool isSlashable =
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
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            IList<ValidatorIndex> bit0Indices = indexedAttestation.CustodyBit0Indices;
            IList<ValidatorIndex> bit1Indices = indexedAttestation.CustodyBit1Indices;

            // Verify no index has custody bit equal to 1 [to be removed in phase 1]
            if (bit1Indices.Count != 0) // [to be removed in phase 1]
            {
                if (_logger.IsWarn()) Log.InvalidIndexedAttestationBit1(_logger, indexedAttestation.Data.Index, indexedAttestation.Data.Slot, bit1Indices.Count(), null);
                return false; //[to be removed in phase 1]
            }

            // Verify max number of indices
            int totalIndices = bit0Indices.Count + bit1Indices.Count;
            if ((ulong) totalIndices > miscellaneousParameters.MaximumValidatorsPerCommittee)
            {
                if (_logger.IsWarn()) Log.InvalidIndexedAttestationTooMany(_logger, indexedAttestation.Data.Index, indexedAttestation.Data.Slot, totalIndices, miscellaneousParameters.MaximumValidatorsPerCommittee, null);
                return false;
            }

            // Verify index sets are disjoint
            IEnumerable<ValidatorIndex> intersect = bit0Indices.Intersect(bit1Indices);
            if (intersect.Count() != 0)
            {
                if (_logger.IsWarn()) Log.InvalidIndexedAttestationIntersection(_logger, indexedAttestation.Data.Index, indexedAttestation.Data.Slot, intersect.Count(), null);
                return false;
            }

            // Verify indices are sorted
            if (bit0Indices.Count() > 1)
            {
                for (int index = 0; index < bit0Indices.Count() - 1; index++)
                {
                    if (!(bit0Indices[index] < bit0Indices[index + 1]))
                    {
                        if (_logger.IsWarn()) Log.InvalidIndexedAttestationNotSorted(_logger, indexedAttestation.Data.Index, indexedAttestation.Data.Slot, 0, index, null);
                        return false;
                    }
                }
            }

            if (bit1Indices.Count() > 1)
            {
                for (int index = 0; index < bit1Indices.Count() - 1; index++)
                {
                    if (!(bit1Indices[index] < bit1Indices[index + 1]))
                    {
                        if (_logger.IsWarn()) Log.InvalidIndexedAttestationNotSorted(_logger, indexedAttestation.Data.Index, indexedAttestation.Data.Slot, 1, index, null);
                        return false;
                    }
                }
            }

            // Verify aggregate signature
            IEnumerable<BlsPublicKey> bit0PublicKeys = bit0Indices.Select(x => state.Validators[(int) (ulong) x].PublicKey);
            BlsPublicKey bit0AggregatePublicKey = _cryptographyService.BlsAggregatePublicKeys(bit0PublicKeys);
            IEnumerable<BlsPublicKey> bit1PublicKeys = bit1Indices.Select(x => state.Validators[(int) (ulong) x].PublicKey);
            BlsPublicKey bit1AggregatePublicKey = _cryptographyService.BlsAggregatePublicKeys(bit1PublicKeys);
            BlsPublicKey[] publicKeys = new[] {bit0AggregatePublicKey, bit1AggregatePublicKey};

            AttestationDataAndCustodyBit attestationDataAndCustodyBit0 = new AttestationDataAndCustodyBit(indexedAttestation.Data, false);
            Hash32 messageHashBit0 = attestationDataAndCustodyBit0.HashTreeRoot();
            AttestationDataAndCustodyBit attestationDataAndCustodyBit1 = new AttestationDataAndCustodyBit(indexedAttestation.Data, true);
            Hash32 messageHashBit1 = attestationDataAndCustodyBit1.HashTreeRoot();
            Hash32[] messageHashes = new[] {messageHashBit0, messageHashBit1};

            BlsSignature signature = indexedAttestation.Signature;

            bool isValid = _cryptographyService.BlsVerifyMultiple(publicKeys, messageHashes, signature, domain);
            if (!isValid)
            {
                if (_logger.IsWarn()) Log.InvalidIndexedAttestationSignature(_logger, indexedAttestation.Data.Index, indexedAttestation.Data.Slot, null);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if 'leaf' at 'index' verifies against the Merkle 'root' and 'branch'
        /// </summary>
        public bool IsValidMerkleBranch(Hash32 leaf, IReadOnlyList<Hash32> branch, int depth, ulong index, Hash32 root)
        {
            Hash32 value = leaf;
            for (int testDepth = 0; testDepth < depth; testDepth++)
            {
                Hash32 branchValue = branch[testDepth];
                ulong indexAtDepth = index / ((ulong) 1 << testDepth);
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