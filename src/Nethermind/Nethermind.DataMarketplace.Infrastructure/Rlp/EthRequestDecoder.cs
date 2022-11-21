// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class EthRequestDecoder : IRlpNdmDecoder<EthRequest>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static EthRequestDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(EthRequest)] = new EthRequestDecoder();
        }

        public EthRequest Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                Keccak id = rlpStream.DecodeKeccak();
                string host = rlpStream.DecodeString();
                Address address = rlpStream.DecodeAddress();
                UInt256 value = rlpStream.DecodeUInt256();
                DateTime requestedAt = DateTimeOffset.FromUnixTimeSeconds(rlpStream.DecodeLong()).UtcDateTime;
                Keccak transactionHash = rlpStream.DecodeKeccak();

                return new EthRequest(id, host, address, value, requestedAt, transactionHash);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(EthRequest)} cannot be deserialized from", e);
            }
        }

        public void Encode(RlpStream stream, EthRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(EthRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.Host),
                Serialization.Rlp.Rlp.Encode(item.Address),
                Serialization.Rlp.Rlp.Encode(item.Value),
                Serialization.Rlp.Rlp.Encode(new DateTimeOffset(item.RequestedAt).ToUnixTimeSeconds()),
                Serialization.Rlp.Rlp.Encode(item.TransactionHash));
        }

        public int GetLength(EthRequest item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
