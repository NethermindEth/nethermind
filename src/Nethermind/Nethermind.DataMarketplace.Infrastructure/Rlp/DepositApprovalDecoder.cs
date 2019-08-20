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
    public class DepositApprovalDecoder : IRlpDecoder<DepositApproval>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        public DepositApprovalDecoder()
        {
        }

        static DepositApprovalDecoder()
        {
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(DepositApproval)] = new DepositApprovalDecoder();
        }

        public DepositApproval Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var id = rlpStream.DecodeKeccak();
            var assetId = rlpStream.DecodeKeccak();
            var assetName = rlpStream.DecodeString();
            var kyc = rlpStream.DecodeString();
            var consumer = rlpStream.DecodeAddress();
            var provider = rlpStream.DecodeAddress();
            var timestamp = rlpStream.DecodeUlong();
            var state = (DepositApprovalState) rlpStream.DecodeInt();

            return new DepositApproval(id, assetId, assetName, kyc, consumer, provider, timestamp, state);
        }

        public Nethermind.Core.Encoding.Rlp Encode(DepositApproval item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Id),
                Nethermind.Core.Encoding.Rlp.Encode(item.AssetId),
                Nethermind.Core.Encoding.Rlp.Encode(item.AssetName),
                Nethermind.Core.Encoding.Rlp.Encode(item.Kyc),
                Nethermind.Core.Encoding.Rlp.Encode(item.Consumer),
                Nethermind.Core.Encoding.Rlp.Encode(item.Provider),
                Nethermind.Core.Encoding.Rlp.Encode(item.Timestamp),
                Nethermind.Core.Encoding.Rlp.Encode((int) item.State));
        }

        public void Encode(MemoryStream stream, DepositApproval item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DepositApproval item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}