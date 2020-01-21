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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AuRa.Validators
{
    internal class PendingValidatorsDecoder : IRlpDecoder<PendingValidators>
    {
        public PendingValidators Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            var sequenceLength = rlpStream.ReadSequenceLength();
            var pendingValidatorsCheck = rlpStream.Position + sequenceLength;

            var blockNumber = rlpStream.DecodeLong();
            var blockHash = rlpStream.DecodeKeccak();

            var addressSequenceLength = rlpStream.ReadSequenceLength();
            var addressCheck = rlpStream.Position + addressSequenceLength;
            List<Address> addresses = new List<Address>();
            while (rlpStream.Position < addressCheck)
            {
                addresses.Add(rlpStream.DecodeAddress());
            }
            rlpStream.Check(addressCheck);

            var result = new PendingValidators(blockNumber, blockHash, addresses.ToArray())
            {
                AreFinalized = rlpStream.DecodeBool()
            };

            rlpStream.Check(pendingValidatorsCheck);

            return result;
        }

        public Rlp Encode(PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(MemoryStream stream, PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (int contentLength, int addressesLength) = GetContentLength(item, rlpBehaviors);
            Rlp.StartSequence(stream, contentLength);
            Rlp.Encode(stream, item.BlockNumber);
            Rlp.Encode(stream, item.BlockHash);
            Rlp.StartSequence(stream, addressesLength);
            for (int i = 0; i < item.Addresses.Length; i++)
            {
                Rlp.Encode(stream, item.Addresses[i]);
            }
            Rlp.Encode(stream, item.AreFinalized);
        }

        public void Encode(RlpStream rlpStream, PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (int contentLength, int addressesLength) = GetContentLength(item, rlpBehaviors);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(item.BlockNumber);
            rlpStream.Encode(item.BlockHash);
            rlpStream.StartSequence(addressesLength);
            for (int i = 0; i < item.Addresses.Length; i++)
            {
                rlpStream.Encode(item.Addresses[i]);
            }
            rlpStream.Encode(item.AreFinalized);
        }

        public int GetLength(PendingValidators item, RlpBehaviors rlpBehaviors) =>
            item == null ? 1 : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);

        private (int Total, int Addresses) GetContentLength(PendingValidators item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = Rlp.LengthOf(item.BlockNumber) 
                                + Rlp.LengthOf(item.BlockHash) 
                                + Rlp.LengthOf(item.AreFinalized); 

            var addressesLength = GetAddressesLength(item.Addresses);
            contentLength += Rlp.LengthOfSequence(addressesLength);

            return (contentLength, addressesLength);
        }

        private int GetAddressesLength(Address[] addresses) => addresses.Sum(Rlp.LengthOf);
    }
} 