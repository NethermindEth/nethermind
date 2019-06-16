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
using Nethermind.Core.Encoding;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataHeaderProviderDecoder : IRlpDecoder<DataHeaderProvider>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        private DataHeaderProviderDecoder()
        {
        }

        static DataHeaderProviderDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(DataHeaderProvider)] = new DataHeaderProviderDecoder();
        }

        public DataHeaderProvider Decode(Nethermind.Core.Encoding.Rlp.DecoderContext context,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = context.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var address = context.DecodeAddress();
            var name = context.DecodeString();

            return new DataHeaderProvider(address, name);
        }

        public Nethermind.Core.Encoding.Rlp Encode(DataHeaderProvider item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Address),
                Nethermind.Core.Encoding.Rlp.Encode(item.Name));
        }

        public void Encode(MemoryStream stream, DataHeaderProvider item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DataHeaderProvider item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}