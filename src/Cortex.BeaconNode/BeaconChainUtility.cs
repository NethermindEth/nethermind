using System;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode
{
    public class BeaconChainUtility
    {
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public BeaconChainUtility(IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            ICryptographyService cryptographyService)
        {
            _cryptographyService = cryptographyService;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _timeParameterOptions = timeParameterOptions;
        }

        /// <summary>
        /// Return the epoch number of ``slot``.
        /// </summary>
        public Epoch ComputeEpochOfSlot(Slot slot)
        {
            return new Epoch(slot / _timeParameterOptions.CurrentValue.SlotsPerEpoch);
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
        /// Return the start slot of 'epoch'
        /// </summary>
        public Slot ComputeStartSlotOfEpoch(Epoch epoch)
        {
            return _timeParameterOptions.CurrentValue.SlotsPerEpoch * (ulong)epoch;
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

        /// <summary>
        /// Return the committee corresponding to ``indices``, ``seed``, ``index``, and committee ``count``.
        /// </summary>
        public IReadOnlyList<ValidatorIndex> ComputeCommittee(IList<ValidatorIndex> indices, Hash32 seed, Shard index, ulong committeeCount)
        {
            var start = ((ulong)indices.Count * (ulong)index) / committeeCount;
            var end = ((ulong)indices.Count * ((ulong)index + 1)) / committeeCount;
            //var count = indices.Count;
            var shuffled = new List<ValidatorIndex>();
            for (var i = start; i < end; i++)
            {
                var shuffledLookup = ComputeShuffledIndex(new ValidatorIndex(i), (ulong)indices.Count, seed);
                var shuffledIndex =  indices[(int)(ulong)shuffledLookup];
                shuffled.Add(shuffledIndex);
            }
            return shuffled;
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
    }
}
