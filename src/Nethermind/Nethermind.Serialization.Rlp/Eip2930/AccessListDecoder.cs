// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp.Eip2930
{
    public sealed class AccessListDecoder : RlpValueDecoder<AccessList?>
    {
        private const int IndexLength = 32;

        public static readonly AccessListDecoder Instance = new();

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(AccessListDecoder))]
        public AccessListDecoder() { }

        protected override AccessList? DecodeInternal(
            ref Rlp.ValueDecoderContext decoderContext,
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
                Address address = decoderContext.DecodeAddress() ?? throw new RlpException("Invalid tx access list format - address is null");
                accessListBuilder.AddAddress(address);

                if (decoderContext.Position < check)
                {
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
                    }
                }
                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                {
                    decoderContext.Check(accessListItemCheck);
                }
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(check);
            }

            return accessListBuilder.Build();
        }

        public override void Encode(RlpStream stream, AccessList? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.WriteByte(Rlp.EmptyListByte);
                return;
            }

            int contentLength = GetContentLength(item);
            stream.StartSequence(contentLength);
            foreach ((Address? address, AccessList.StorageKeysEnumerable storageKeys) in item)
            {
                // {} brackets applied to show the content structure
                // Address
                //   Index1
                //   Index2
                //   ...
                //   IndexN
                AccessItemLengths lengths = new(storageKeys.Count);
                stream.StartSequence(lengths.ContentLength);
                {
                    stream.Encode(address);
                    stream.StartSequence(lengths.IndexesContentLength);
                    {
                        foreach (UInt256 index in storageKeys)
                        {
                            // storage indices are encoded as 32 bytes data arrays
                            stream.Encode(index, IndexLength);
                        }
                    }
                }
            }
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
