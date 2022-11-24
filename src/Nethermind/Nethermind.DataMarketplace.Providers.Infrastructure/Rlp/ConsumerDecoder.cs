// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rlp
{
    internal class ConsumerDecoder : IRlpNdmDecoder<Consumer>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static ConsumerDecoder()
        {
            Nethermind.Serialization.Rlp.Rlp.Decoders[typeof(Consumer)] = new ConsumerDecoder();
        }

        public Consumer Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            _ = rlpStream.ReadSequenceLength();
            var depositId = rlpStream.DecodeKeccak();
            var verificationTimestamp = rlpStream.DecodeUInt();
            var dataRequest = Nethermind.Serialization.Rlp.Rlp.Decode<DataRequest>(rlpStream);
            var dataAsset = Nethermind.Serialization.Rlp.Rlp.Decode<DataAsset>(rlpStream);
            var hasAvailableUnits = rlpStream.DecodeBool();

            return new Consumer(depositId, verificationTimestamp, dataRequest, dataAsset, hasAvailableUnits);
        }

        public Nethermind.Serialization.Rlp.Rlp Encode(Consumer? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Nethermind.Serialization.Rlp.Rlp.Encode(
                Nethermind.Serialization.Rlp.Rlp.Encode(item.DepositId),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.VerificationTimestamp),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.DataRequest),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.DataAsset),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.HasAvailableUnits));
        }

        public int GetLength(Consumer item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }

        public void Encode(RlpStream stream, Consumer item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }
    }
}
