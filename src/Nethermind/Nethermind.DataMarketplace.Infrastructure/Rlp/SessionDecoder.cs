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

using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class SessionDecoder : IRlpDecoder<Session>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static SessionDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(Session)] = new SessionDecoder();
        }

        public Session Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

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

            return new Session(id, depositId, dataAssetId, consumerAddress, consumerNodeId, providerAddress,
                providerNodeId, state,  startUnitsFromProvider, startUnitsFromConsumer, startTimestamp, finishTimestamp,
                consumedUnits, unpaidUnits, paidUnits, settledUnits);
        }

        public Nethermind.Core.Encoding.Rlp Encode(Session item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Id),
                Nethermind.Core.Encoding.Rlp.Encode(item.DepositId),
                Nethermind.Core.Encoding.Rlp.Encode(item.DataAssetId),
                Nethermind.Core.Encoding.Rlp.Encode(item.ConsumerAddress),
                Nethermind.Core.Encoding.Rlp.Encode(item.ConsumerNodeId.Bytes),
                Nethermind.Core.Encoding.Rlp.Encode(item.ProviderAddress),
                Nethermind.Core.Encoding.Rlp.Encode(item.ProviderNodeId.Bytes),
                Nethermind.Core.Encoding.Rlp.Encode((int) item.State),
                Nethermind.Core.Encoding.Rlp.Encode(item.StartUnitsFromProvider),
                Nethermind.Core.Encoding.Rlp.Encode(item.StartUnitsFromConsumer),
                Nethermind.Core.Encoding.Rlp.Encode(item.StartTimestamp),
                Nethermind.Core.Encoding.Rlp.Encode(item.FinishTimestamp),
                Nethermind.Core.Encoding.Rlp.Encode(item.ConsumedUnits),
                Nethermind.Core.Encoding.Rlp.Encode(item.UnpaidUnits),
                Nethermind.Core.Encoding.Rlp.Encode(item.PaidUnits),
                Nethermind.Core.Encoding.Rlp.Encode(item.SettledUnits));
        }

        public void Encode(MemoryStream stream, Session item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(Session item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}