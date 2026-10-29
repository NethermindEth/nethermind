// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp.Eip2930
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AccessListDecoder))]
    public sealed class AccessListDecoder() : RlpDecoder<AccessList?>
    {
        private const int IndexLength = 32;

        public static readonly AccessListDecoder Instance = new();

        protected override AccessList? DecodeInternal(
            ref RlpReader reader,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

        private static AccessList? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
        {
            if (reader.TryReadEmptySequence())
            {
                return null;
            }

            RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int length);

            AccessList.Builder accessListBuilder = new();
            while (items.HasNext)
            {
                RlpReader item = items.EnterSequence<TCanonicalChecks>(out int accessListItemLength);
                Address address = item.DecodeAddress();
                accessListBuilder.AddAddress(address);

                if (!item.HasNext)
                {
                    throw new RlpReaderException("Invalid tx access list format - storage keys missing");
                }

                RlpReader storages = item.EnterSequence<TCanonicalChecks>(out int storagesLength);
                while (storages.HasNext)
                {
                    UInt256 index = storages.DecodeUInt256<TCanonicalChecks>(IndexLength);
                    accessListBuilder.AddStorage(index);
                }

                item.ExitSequence(in storages, storagesLength, rlpBehaviors);
                items.ExitSequence(in item, accessListItemLength, rlpBehaviors);
            }

            reader.ExitSequence(in items, length, rlpBehaviors);
            return accessListBuilder.Build();
        }

        public override void Encode(ref RlpWriter writer, AccessList? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            int contentLength = GetContentLength(item);
            RlpSequence sequence = writer.StartSequence(contentLength);
            foreach ((Address? address, AccessList.StorageKeysEnumerable storageKeys) in item)
            {
                // {} brackets applied to show the content structure
                // Address
                //   Index1
                //   Index2
                //   ...
                //   IndexN
                AccessItemLengths lengths = new(storageKeys.Count);
                RlpSequence itemSequence = writer.StartSequence(lengths.ContentLength);
                {
                    writer.Encode(address);
                    RlpSequence indexesSequence = writer.StartSequence(lengths.IndexesContentLength);
                    {
                        foreach (UInt256 index in storageKeys)
                        {
                            // storage indices are encoded as 32 bytes data arrays
                            writer.Encode(index, IndexLength);
                        }
                    }
                    writer.EndSequence(indexesSequence);
                }
                writer.EndSequence(itemSequence);
            }

            writer.EndSequence(sequence);
        }

        public override int GetLength(AccessList? accessList, RlpBehaviors rlpBehaviors)
        {
            if (accessList is null)
            {
                return 1;
            }

            int contentLength = GetContentLength(accessList);
            return Rlp.LengthOfSequence(contentLength);
        }

        /// <summary>
        /// Helper class to store the content lengths calculation.
        /// </summary>
        private readonly struct AccessItemLengths
        {
            public AccessItemLengths(int indexesCount)
            {
                IndexesContentLength = indexesCount * Rlp.LengthOfKeccakRlp;
                ContentLength = Rlp.LengthOfSequence(IndexesContentLength) + Rlp.LengthOfAddressRlp;
                SequenceLength = Rlp.LengthOfSequence(ContentLength);
            }

            public int IndexesContentLength { get; }

            public int ContentLength { get; }

            public int SequenceLength { get; }
        }

        private static int GetContentLength(AccessList accessList)
        {
            int sum = 0;
            foreach ((Address Address, AccessList.StorageKeysEnumerable StorageKeys) entry in accessList)
            {
                int indexesContentLength = entry.StorageKeys.Count * Rlp.LengthOfKeccakRlp;
                int contentLength = Rlp.LengthOfSequence(indexesContentLength) + Rlp.LengthOfAddressRlp;
                sum += Rlp.LengthOfSequence(contentLength);
            }

            return sum;
        }
    }
}
