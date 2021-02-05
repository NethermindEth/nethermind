//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public class AccessListDecoder : IRlpStreamDecoder<AccessList?>
    {
        private readonly HashSet<UInt256> _emptyStorages = new();

        public AccessList? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            int length = rlpStream.PeekNextRlpLength();
            int check = rlpStream.Position + length;
            rlpStream.SkipLength();
            Dictionary<Address, IReadOnlySet<UInt256>> data = new();
            while (rlpStream.Position < check)
            {
                rlpStream.SkipLength();
                Address address = rlpStream.DecodeAddress();
                if (address == null)
                {
                    throw new RlpException("Invalid tx access list format - address is null");
                }

                HashSet<UInt256> storages = _emptyStorages;
                if (rlpStream.Position < check)
                {
                    int storageCheck = rlpStream.Position + rlpStream.PeekNextRlpLength();
                    rlpStream.SkipLength();
                    storages = new HashSet<UInt256>();
                    while (rlpStream.Position < storageCheck)
                    {
                        UInt256 index = rlpStream.DecodeUInt256();
                        storages.Add(index);
                    }
                }

                data[address] = storages;
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraData) != RlpBehaviors.AllowExtraData)
            {
                rlpStream.Check(check);
            }

            return new AccessList(data);
        }

        public void Encode(RlpStream stream, AccessList? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.WriteByte(Rlp.NullObjectByte);
            }
            else
            {
                int contentLength = GetContentLength(item, rlpBehaviors);
                stream.StartSequence(contentLength);

                foreach ((Address address, IReadOnlySet<UInt256> indexes) in item.Data)
                {
                    int oneItemContentLength = GetOneItemContentLength(indexes);
                    stream.StartSequence(oneItemContentLength);
                    stream.Encode(address);
                    int indexesContentLength = GetIndexesContentLength(indexes);
                    stream.StartSequence(indexesContentLength);
                    foreach (UInt256 index in indexes)
                    {
                        stream.Encode(index);
                    }
                }
            }
        }

        private static int GetContentLength(AccessList? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return 0;
            }

            int contentLength = 0;

            foreach ((_, IReadOnlySet<UInt256> indexes) in item.Data)
            {
                int oneItemContentLength = GetOneItemContentLength(indexes);
                contentLength += Rlp.LengthOfSequence(oneItemContentLength);
            }

            return contentLength;
        }

        private static int GetOneItemContentLength(IReadOnlySet<UInt256> indexes)
        {
            int oneItemContentLength = 0;
            oneItemContentLength += Rlp.LengthOfAddressRlp;
            int indexesContentLength = GetIndexesContentLength(indexes);
            oneItemContentLength += Rlp.LengthOfSequence(indexesContentLength);
            return oneItemContentLength;
        }

        private static int GetIndexesContentLength(IReadOnlySet<UInt256> indexes)
        {
            int indexesContentLength = 0;
            foreach (UInt256 index in indexes)
            {
                indexesContentLength += Rlp.LengthOf(index);
            }

            return indexesContentLength;
        }

        public int GetLength(AccessList? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return 0;
            }

            return Rlp.GetSequenceRlpLength(GetContentLength(item, rlpBehaviors));
        }
    }
}
