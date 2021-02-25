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
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class UnitsRangeDecoder : IRlpNdmDecoder<UnitsRange>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static UnitsRangeDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(UnitsRange)] = new UnitsRangeDecoder();
        }

        public UnitsRange Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            try
            {
                uint from = rlpStream.DecodeUInt();
                uint to = rlpStream.DecodeUInt();

                return new UnitsRange(from, to);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(UnitsRange)} could not be decoded", e);
            }
        }

        public void Encode(RlpStream stream, UnitsRange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(UnitsRange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.From),
                Serialization.Rlp.Rlp.Encode(item.To));
        }

        public int GetLength(UnitsRange item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
