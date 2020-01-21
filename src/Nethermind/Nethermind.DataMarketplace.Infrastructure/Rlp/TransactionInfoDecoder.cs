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

using System.IO;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class TransactionInfoDecoder : IRlpDecoder<TransactionInfo>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        private TransactionInfoDecoder()
        {
        }

        static TransactionInfoDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(TransactionInfo)] = new TransactionInfoDecoder();
        }
        
        public TransactionInfo Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            var hash = rlpStream.DecodeKeccak();
            var value = rlpStream.DecodeUInt256();
            var gasPrice = rlpStream.DecodeUInt256();
            var gasLimit = rlpStream.DecodeUlong();
            var timestamp = rlpStream.DecodeUlong();
            var type = (TransactionType) rlpStream.DecodeInt();
            var state = (TransactionState) rlpStream.DecodeInt();

            return new TransactionInfo(hash, value, gasPrice, gasLimit, timestamp, type, state);
        }

        public Serialization.Rlp.Rlp Encode(TransactionInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Hash),
                Serialization.Rlp.Rlp.Encode(item.Value),
                Serialization.Rlp.Rlp.Encode(item.GasPrice),
                Serialization.Rlp.Rlp.Encode(item.GasLimit),
                Serialization.Rlp.Rlp.Encode(item.Timestamp),
                Serialization.Rlp.Rlp.Encode((int) item.Type),
                Serialization.Rlp.Rlp.Encode((int) item.State));
        }

        public void Encode(MemoryStream stream, TransactionInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(TransactionInfo item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}