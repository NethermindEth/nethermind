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