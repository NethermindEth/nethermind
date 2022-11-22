// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rlp
{
    internal class ProviderSessionDecoder : IRlpNdmDecoder<ProviderSession>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static ProviderSessionDecoder()
        {
            Nethermind.Serialization.Rlp.Rlp.Decoders[typeof(ProviderSession)] = new ProviderSessionDecoder();
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

        public Nethermind.Serialization.Rlp.Rlp Encode(ProviderSession? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Nethermind.Serialization.Rlp.Rlp.Encode(
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Id),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.DepositId),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.DataAssetId),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ConsumerAddress),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ConsumerNodeId.Bytes),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ProviderAddress),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ProviderNodeId.Bytes),
                Nethermind.Serialization.Rlp.Rlp.Encode((int)item.State),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.StartUnitsFromProvider),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.StartUnitsFromConsumer),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.StartTimestamp),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.FinishTimestamp),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ConsumedUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.UnpaidUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.PaidUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.SettledUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.GraceUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode((int)item.DataAvailability));
        }

        public int GetLength(ProviderSession item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }

        public void Encode(RlpStream stream, ProviderSession item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }
    }
}
