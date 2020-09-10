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
