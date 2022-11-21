// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.RocksDbExtractor.ProviderDecoders.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.RocksDbExtractor.ProviderDecoders
{
    internal class ConsumerDecoder : IRlpDecoder<Consumer>
    {
        static ConsumerDecoder()
        {
            Rlp.Decoders[typeof(Consumer)] = new ConsumerDecoder();
        }

        public Consumer Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            _ = rlpStream.ReadSequenceLength();
            var depositId = rlpStream.DecodeKeccak();
            var verificationTimestamp = rlpStream.DecodeUInt();
            var dataRequest = Rlp.Decode<DataRequest>(rlpStream);
            var dataAsset = Rlp.Decode<DataAsset>(rlpStream);
            var hasAvailableUnits = rlpStream.DecodeBool();

            return new Consumer(depositId, verificationTimestamp, dataRequest, dataAsset, hasAvailableUnits);
        }

        public Rlp Encode(Consumer item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Rlp.Encode(
                Rlp.Encode(item.DepositId),
                Rlp.Encode(item.VerificationTimestamp),
                Rlp.Encode(item.DataRequest),
                Rlp.Encode(item.DataAsset),
                Rlp.Encode(item.HasAvailableUnits));
        }

        public int GetLength(Consumer item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
