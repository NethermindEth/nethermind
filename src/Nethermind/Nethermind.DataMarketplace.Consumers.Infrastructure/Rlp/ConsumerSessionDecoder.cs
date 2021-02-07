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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Sessions.Domain;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp
{
    public class ConsumerSessionDecoder : IRlpNdmDecoder<ConsumerSession>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static ConsumerSessionDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(ConsumerSession)] = new ConsumerSessionDecoder();
        }

        public ConsumerSession Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            Keccak id = rlpStream.DecodeKeccak();
            Keccak depositId = rlpStream.DecodeKeccak();
            Keccak dataAssetId = rlpStream.DecodeKeccak();
            Address consumerAddress = rlpStream.DecodeAddress();
            PublicKey consumerNodeId = new PublicKey(rlpStream.DecodeByteArray());
            Address providerAddress = rlpStream.DecodeAddress();
            PublicKey providerNodeId = new PublicKey(rlpStream.DecodeByteArray());
            SessionState state = (SessionState) rlpStream.DecodeInt();
            uint startUnitsFromProvider = rlpStream.DecodeUInt();
            uint startUnitsFromConsumer = rlpStream.DecodeUInt();
            ulong startTimestamp = rlpStream.DecodeUlong();
            ulong finishTimestamp = rlpStream.DecodeUlong();
            uint consumedUnits = rlpStream.DecodeUInt();
            uint unpaidUnits = rlpStream.DecodeUInt();
            uint paidUnits = rlpStream.DecodeUInt();
            uint settledUnits = rlpStream.DecodeUInt();
            uint consumedUnitsFromProvider = rlpStream.DecodeUInt();
            DataAvailability dataAvailability = (DataAvailability) rlpStream.DecodeInt();

            return new ConsumerSession(id, depositId, dataAssetId, consumerAddress, consumerNodeId, providerAddress,
                providerNodeId, state, startUnitsFromProvider, startUnitsFromConsumer, startTimestamp, finishTimestamp,
                consumedUnits, unpaidUnits, paidUnits, settledUnits, consumedUnitsFromProvider, dataAvailability);
        }

        public void Encode(RlpStream stream, ConsumerSession item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(ConsumerSession item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.DepositId),
                Serialization.Rlp.Rlp.Encode(item.DataAssetId),
                Serialization.Rlp.Rlp.Encode(item.ConsumerAddress),
                Serialization.Rlp.Rlp.Encode(item.ConsumerNodeId.Bytes),
                Serialization.Rlp.Rlp.Encode(item.ProviderAddress),
                Serialization.Rlp.Rlp.Encode(item.ProviderNodeId.Bytes),
                Serialization.Rlp.Rlp.Encode((int) item.State),
                Serialization.Rlp.Rlp.Encode(item.StartUnitsFromProvider),
                Serialization.Rlp.Rlp.Encode(item.StartUnitsFromConsumer),
                Serialization.Rlp.Rlp.Encode(item.StartTimestamp),
                Serialization.Rlp.Rlp.Encode(item.FinishTimestamp),
                Serialization.Rlp.Rlp.Encode(item.ConsumedUnits),
                Serialization.Rlp.Rlp.Encode(item.UnpaidUnits),
                Serialization.Rlp.Rlp.Encode(item.PaidUnits),
                Serialization.Rlp.Rlp.Encode(item.SettledUnits),
                Serialization.Rlp.Rlp.Encode(item.ConsumedUnitsFromProvider),
                Serialization.Rlp.Rlp.Encode((int) item.DataAvailability));
        }
        
        public int GetLength(ConsumerSession item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
