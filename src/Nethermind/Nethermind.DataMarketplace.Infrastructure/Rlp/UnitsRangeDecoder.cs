// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
