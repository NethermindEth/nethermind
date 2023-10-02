// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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

            AccessList.Builder accessListBuilder = new();
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

            return accessListBuilder.Build();
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

            AccessList.Builder accessListBuilder = new();
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

            return accessListBuilder.Build();
        }

        public void Encode(RlpStream stream, AccessList? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.WriteByte(Rlp.NullObjectByte);
                return;
            }

            int contentLength = GetContentLength(item);
            stream.StartSequence(contentLength);
            foreach ((Address Address, IEnumerable<UInt256> StorageKeys) entry in item)
            {
                List<UInt256> storageKeys = entry.StorageKeys.ToList();
                Address address = entry.Address;

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
                            stream.Encode(index, 32);
                        }
                    }
                }
            }
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
            return accessList
                .Select(entry => new AccessItemLengths(entry.StorageKeys.Count()))
                .Sum(lengths => lengths.SequenceLength);
        }
    }
}
