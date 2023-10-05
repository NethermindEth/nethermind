// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip2930
{
    public class AccessListDecoder : IRlpStreamDecoder<AccessList?>, IRlpValueDecoder<AccessList?>
    {
        /// <summary>
        /// We pay a high code quality tax for the performance optimization on RLP.
        /// Adding more RLP decoders is costly (time wise) but the path taken saves a lot of allocations and GC.
        /// Shall we consider code generation for this? We could potentially generate IL from attributes for each
        /// RLP serializable item and keep it as a compiled call available at runtime.
        /// It would be slightly slower but still much faster than what we would get from using dynamic serializers.
        /// </summary>
        public AccessList? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            int length = rlpStream.ReadSequenceLength();
            int check = rlpStream.Position + length;

            AccessListBuilder accessListBuilder = new();
            while (rlpStream.Position < check)
            {
                int accessListItemLength = rlpStream.ReadSequenceLength();
                int accessListItemCheck = rlpStream.Position + accessListItemLength;
                Address address = rlpStream.DecodeAddress();
                if (address is null)
                {
                    throw new RlpException("Invalid tx access list format - address is null");
                }

                accessListBuilder.AddAddress(address);

                if (rlpStream.Position < check)
                {
                    int storagesLength = rlpStream.ReadSequenceLength();
                    int storagesCheck = rlpStream.Position + storagesLength;
                    while (rlpStream.Position < storagesCheck)
                    {
                        int storageItemCheck = rlpStream.Position + 33;
                        UInt256 index = rlpStream.DecodeUInt256();
                        accessListBuilder.AddStorage(index);
                        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                        {
                            rlpStream.Check(storageItemCheck);
                        }
                    }
                    if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                    {
                        rlpStream.Check(storagesCheck);
                    }
                }
                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                {
                    rlpStream.Check(accessListItemCheck);
                }
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                rlpStream.Check(check);
            }

            return accessListBuilder.ToAccessList();
        }

        /// <summary>
        /// We pay a big copy-paste tax to maintain ValueDecoders but we believe that the amount of allocations saved
        /// make it worth it. To be reviewed periodically.
        /// Question to Lukasz here -> would it be fine to always use ValueDecoderContext only?
        /// I believe it cannot be done for the network items decoding and is only relevant for the DB loads.
        /// </summary>
        public AccessList? Decode(
            ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }

            int length = decoderContext.ReadSequenceLength();
            int check = decoderContext.Position + length;

            AccessListBuilder accessListBuilder = new();
            while (decoderContext.Position < check)
            {
                int accessListItemLength = decoderContext.ReadSequenceLength();
                int accessListItemCheck = decoderContext.Position + accessListItemLength;
                Address address = decoderContext.DecodeAddress();
                if (address is null)
                {
                    throw new RlpException("Invalid tx access list format - address is null");
                }

                accessListBuilder.AddAddress(address);

                if (decoderContext.Position < check)
                {
                    int storagesLength = decoderContext.ReadSequenceLength();
                    int storagesCheck = decoderContext.Position + storagesLength;
                    while (decoderContext.Position < storagesCheck)
                    {
                        int storageItemCheck = decoderContext.Position + 33;
                        UInt256 index = decoderContext.DecodeUInt256();
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

            return accessListBuilder.ToAccessList();
        }

        private readonly struct AccessListItem
        {
            public AccessListItem(Address address, List<UInt256> indexes)
            {
                Address = address;
                Indexes = indexes;
            }

            public Address Address { get; }

            public List<UInt256> Indexes { get; }
        }

        public void Encode(RlpStream stream, AccessList? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.WriteByte(Rlp.NullObjectByte);
            }
            else
            {
                int contentLength = GetContentLength(item);
                stream.StartSequence(contentLength);

                if (!item.IsNormalized)
                {
                    AccessListItem? currentItem = default;

                    void SerializeCurrent()
                    {
                        if (currentItem is not null)
                        {
                            AccessListItem toEncode = currentItem.Value;
                            EncodeListItem(stream, toEncode.Address, toEncode.Indexes, toEncode.Indexes.Count);
                        }
                    }

                    foreach (object accessListEntry in item.OrderQueue!)
                    {
                        if (accessListEntry is Address address)
                        {
                            // serialize any element that is not the last
                            SerializeCurrent();
                            currentItem = new AccessListItem(address, new List<UInt256>());
                        }
                        else
                        {
                            if (currentItem is null)
                            {
                                throw new InvalidDataException(
                                    $"{nameof(AccessList)} order looks corrupted - processing index ahead of address");
                            }

                            currentItem.Value.Indexes.Add((UInt256)accessListEntry);
                        }
                    }

                    // serialize the last element
                    SerializeCurrent();
                }
                else
                {
                    foreach ((Address address, IReadOnlySet<UInt256> indexes) in item.Data)
                    {
                        EncodeListItem(stream, address, indexes, indexes.Count);
                    }
                }
            }
        }

        /// <summary>
        /// Spend some time trying to find some base interface like ICountableEnumerable, none of such in .NET Core
        /// </summary>
        private static void EncodeListItem(
            RlpStream stream,
            Address address,
            IEnumerable<UInt256> indexes,
            int indexesCount)
        {
            // {} brackets applied to show the content structure
            // Address
            //   Index1
            //   Index2
            //   ...
            //   IndexN
            AccessItemLengths lengths = new(indexesCount);
            stream.StartSequence(lengths.ContentLength);
            {
                stream.Encode(address);
                stream.StartSequence(lengths.IndexesContentLength);
                {
                    foreach (UInt256 index in indexes)
                    {
                        // storage indices are encoded as 32 bytes data arrays
                        stream.Encode(index, 32);
                    }
                }
            }
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
            int contentLength = 0;
            if (accessList.IsNormalized)
            {
                foreach ((_, IReadOnlySet<UInt256> indexes) in accessList.Data)
                {
                    contentLength += new AccessItemLengths(indexes.Count).SequenceLength;
                }
            }
            else
            {
                IReadOnlyCollection<object> orderQueue = accessList.OrderQueue;
                bool isOpen = false;
                int indexCounter = 0;
                foreach (object accessListEntry in orderQueue!)
                {
                    if (accessListEntry is Address)
                    {
                        if (isOpen)
                        {
                            contentLength += new AccessItemLengths(indexCounter).SequenceLength;
                            indexCounter = 0;
                        }
                        else
                        {
                            isOpen = true;
                        }
                    }
                    else
                    {
                        indexCounter++;
                    }
                }

                if (isOpen)
                {
                    contentLength += new AccessItemLengths(indexCounter).SequenceLength;
                }
            }

            return contentLength;
        }

        public int GetLength(AccessList? accessList, RlpBehaviors rlpBehaviors)
        {
            if (accessList is null)
            {
                return 1;
            }

            int contentLength = GetContentLength(accessList);
            return Rlp.LengthOfSequence(contentLength);
        }

        public Rlp Encode(AccessList? accessList, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(accessList, rlpBehaviors));
            Encode(rlpStream, accessList, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }
    }
}
