// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.RocksDbExtractor.ProviderDecoders.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.RocksDbExtractor.ProviderDecoders
{
    internal class ProviderSessionDecoder : IRlpDecoder<ProviderSession>
    {
        static ProviderSessionDecoder()
        {
            Rlp.Decoders[typeof(ProviderSession)] = new ProviderSessionDecoder();
        }

        public ProviderSession Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            _ = rlpStream.ReadSequenceLength();
            var id = rlpStream.DecodeKeccak();
            var depositId = rlpStream.DecodeKeccak();
            var dataAssetId = rlpStream.DecodeKeccak();
            var consumerAddress = rlpStream.DecodeAddress();
            var consumerNodeId = new PublicKey(rlpStream.DecodeByteArray());
            var providerAddress = rlpStream.DecodeAddress();
            var providerNodeId = new PublicKey(rlpStream.DecodeByteArray());
            var state = (SessionState)rlpStream.DecodeInt();
            var startUnitsFromProvider = rlpStream.DecodeUInt();
            var startUnitsFromConsumer = rlpStream.DecodeUInt();
            var startTimestamp = rlpStream.DecodeUlong();
            var finishTimestamp = rlpStream.DecodeUlong();
            var consumedUnits = rlpStream.DecodeUInt();
            var unpaidUnits = rlpStream.DecodeUInt();
            var paidUnits = rlpStream.DecodeUInt();
            var settledUnits = rlpStream.DecodeUInt();
            var graceUnits = rlpStream.DecodeUInt();
            var dataAvailability = (DataAvailability)rlpStream.DecodeInt();

            return new ProviderSession(id, depositId, dataAssetId, consumerAddress, consumerNodeId, providerAddress,
                providerNodeId, state, startUnitsFromProvider, startUnitsFromConsumer, startTimestamp, finishTimestamp,
                consumedUnits, unpaidUnits, paidUnits, settledUnits, graceUnits, dataAvailability);
        }

        public Rlp Encode(ProviderSession item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Rlp.Encode(
                Rlp.Encode(item.Id),
                Rlp.Encode(item.DepositId),
                Rlp.Encode(item.DataAssetId),
                Rlp.Encode(item.ConsumerAddress),
                Rlp.Encode(item.ConsumerNodeId.Bytes),
                Rlp.Encode(item.ProviderAddress),
                Rlp.Encode(item.ProviderNodeId.Bytes),
                Rlp.Encode((int)item.State),
                Rlp.Encode(item.StartUnitsFromProvider),
                Rlp.Encode(item.StartUnitsFromConsumer),
                Rlp.Encode(item.StartTimestamp),
                Rlp.Encode(item.FinishTimestamp),
                Rlp.Encode(item.ConsumedUnits),
                Rlp.Encode(item.UnpaidUnits),
                Rlp.Encode(item.PaidUnits),
                Rlp.Encode(item.SettledUnits),
                Rlp.Encode(item.GraceUnits),
                Rlp.Encode((int)item.DataAvailability));
        }

        public int GetLength(ProviderSession item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
