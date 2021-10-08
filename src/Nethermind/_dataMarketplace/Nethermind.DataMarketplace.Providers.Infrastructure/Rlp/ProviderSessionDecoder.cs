/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
            var state = (SessionState) rlpStream.DecodeInt();
            var startUnitsFromProvider = rlpStream.DecodeUInt();
            var startUnitsFromConsumer = rlpStream.DecodeUInt();
            var startTimestamp = rlpStream.DecodeUlong();
            var finishTimestamp = rlpStream.DecodeUlong();
            var consumedUnits = rlpStream.DecodeUInt();
            var unpaidUnits = rlpStream.DecodeUInt();
            var paidUnits = rlpStream.DecodeUInt();
            var settledUnits = rlpStream.DecodeUInt();
            var graceUnits = rlpStream.DecodeUInt();
            var dataAvailability = (DataAvailability) rlpStream.DecodeInt();

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
                Nethermind.Serialization.Rlp.Rlp.Encode((int) item.State),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.StartUnitsFromProvider),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.StartUnitsFromConsumer),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.StartTimestamp),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.FinishTimestamp),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ConsumedUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.UnpaidUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.PaidUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.SettledUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.GraceUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode((int) item.DataAvailability));
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