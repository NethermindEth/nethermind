// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DepositDecoder : IRlpNdmDecoder<Deposit>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DepositDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(Deposit)] = new DepositDecoder();
        }

        public Deposit Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            Keccak id = rlpStream.DecodeKeccak();
            uint units = rlpStream.DecodeUInt();
            uint expiryTime = rlpStream.DecodeUInt();
            UInt256 value = rlpStream.DecodeUInt256();

            return new Deposit(id, units, expiryTime, value);
        }

        public void Encode(RlpStream stream, Deposit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(Deposit item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.Units),
                Serialization.Rlp.Rlp.Encode(item.ExpiryTime),
                Serialization.Rlp.Rlp.Encode(item.Value));
        }

        public int GetLength(Deposit item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
