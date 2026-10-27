// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp.Eip2930
{
    [method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AccessListDecoder))]
    public sealed class AccessListDecoder() : RlpDecoder<AccessList?>
    {
        private const int IndexLength = 32;

        public static readonly AccessListDecoder Instance = new();

        protected override AccessList? DecodeInternal(
            ref RlpReader decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int length = decoderContext.ReadSequenceLength();
            int check = decoderContext.Position + length;

            AccessList.Builder accessListBuilder = new();
            while (decoderContext.Position < check)
            {
                int accessListItemLength = decoderContext.ReadSequenceLength();
                int accessListItemCheck = decoderContext.Position + accessListItemLength;
                Address address = decoderContext.DecodeAddress();
                accessListBuilder.AddAddress(address);

                if (decoderContext.Position >= accessListItemCheck)
                {
                    throw new RlpException("Invalid tx access list format - storage keys missing");
                }

                int storagesLength = decoderContext.ReadSequenceLength();
                int storagesCheck = decoderContext.Position + storagesLength;
                while (decoderContext.Position < storagesCheck)
                {
                    int storageItemCheck = decoderContext.Position + IndexLength + 1;
                    UInt256 index = decoderContext.DecodeUInt256(IndexLength);
                    accessListBuilder.AddStorage(index);

                    if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                    {
                        decoderContext.Check(storageItemCheck);
                    }
                }

                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                {
                    decoderContext.Check(storagesCheck);
                    decoderContext.Check(accessListItemCheck);
                }
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(check);
            }

            return accessListBuilder.Build();
        }

        public override void Encode<TWriter>(AccessList? item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.WriteByte(Rlp.EmptyListByte);
                return;
            }

            writer.StartSequence(lengths.Pop());
            foreach ((Address? address, AccessList.StorageKeysEnumerable storageKeys) in item)
            {
                // {} brackets applied to show the content structure
                // Address
                //   Index1
                //   Index2
                //   ...
                //   IndexN
                AccessItemLengths itemLengths = new(storageKeys.Count);
                writer.StartSequence(itemLengths.ContentLength);
                {
                    writer.Encode(address);
                    writer.StartSequence(itemLengths.IndexesContentLength);
                    {
                        foreach (UInt256 index in storageKeys)
                        {
                            // storage indices are encoded as 32 bytes data arrays
                            writer.Encode(index, IndexLength);
                        }
                    }
                }
            }
        }

        public override void PopulateLength(AccessList? accessList, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (accessList is null)
            {
                lengths.Push(1);
                return;
            }

            int contentLength = GetContentLength(accessList);
            lengths.Push(contentLength);
            lengths.Push(Rlp.LengthOfSequence(contentLength));
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
