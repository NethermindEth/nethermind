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
