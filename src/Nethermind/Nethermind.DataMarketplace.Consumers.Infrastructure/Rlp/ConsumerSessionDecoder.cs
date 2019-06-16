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
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp
{
    public class ConsumerSessionDecoder : IRlpDecoder<ConsumerSession>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static ConsumerSessionDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(ConsumerSession)] = new ConsumerSessionDecoder();
        }

        public ConsumerSession Decode(Nethermind.Core.Encoding.Rlp.DecoderContext context,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = context.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var id = context.DecodeKeccak();
            var depositId = context.DecodeKeccak();
            var dataHeaderId = context.DecodeKeccak();
            var consumerAddress = context.DecodeAddress();
            var consumerNodeId = new PublicKey(context.DecodeByteArray());
            var providerAddress = context.DecodeAddress();
            var providerNodeId = new PublicKey(context.DecodeByteArray());
            var state = (SessionState) context.DecodeInt();
            var startUnitsFromProvider = context.DecodeUInt();
            var startUnitsFromConsumer = context.DecodeUInt();
            var startTimestamp = context.DecodeUlong();
            var finishTimestamp = context.DecodeUlong();
            var consumedUnits = context.DecodeUInt();
            var unpaidUnits = context.DecodeUInt();
            var paidUnits = context.DecodeUInt();
            var settledUnits = context.DecodeUInt();
            var consumedUnitsFromProvider = context.DecodeUInt();
            var dataAvailability = (DataAvailability) context.DecodeInt();
            var streamEnabled = context.DecodeBool();
            var subscriptions = context.DecodeArray(c => c.DecodeString());

            return new ConsumerSession(id, depositId, dataHeaderId, consumerAddress, consumerNodeId, providerAddress,
                providerNodeId, state, startUnitsFromProvider, startUnitsFromConsumer, startTimestamp, finishTimestamp,
                consumedUnits, unpaidUnits, paidUnits, settledUnits, consumedUnitsFromProvider, dataAvailability,
                streamEnabled, subscriptions);
        }

        public Nethermind.Core.Encoding.Rlp Encode(ConsumerSession item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Id),
                Nethermind.Core.Encoding.Rlp.Encode(item.DepositId),
                Nethermind.Core.Encoding.Rlp.Encode(item.DataHeaderId),
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
                Nethermind.Core.Encoding.Rlp.Encode(item.SettledUnits),
                Nethermind.Core.Encoding.Rlp.Encode(item.ConsumedUnitsFromProvider),
                Nethermind.Core.Encoding.Rlp.Encode((int) item.DataAvailability),
                Nethermind.Core.Encoding.Rlp.Encode(item.StreamEnabled),
                Nethermind.Core.Encoding.Rlp.Encode(item.Subscriptions));
        }

        public void Encode(MemoryStream stream, ConsumerSession item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(ConsumerSession item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}