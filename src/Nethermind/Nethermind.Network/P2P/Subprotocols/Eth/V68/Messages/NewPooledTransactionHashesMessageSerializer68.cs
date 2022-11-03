//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages
{
    public class NewPooledTransactionHashesMessageSerializer
        : IZeroMessageSerializer<NewPooledTransactionHashesMessage68>
    {
        public NewPooledTransactionHashesMessage68 Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            TxType[] types = rlpStream.DecodeArray(item => (TxType)item.DecodeByte());
            uint[] sizes = rlpStream.DecodeArray(item => item.DecodeUInt());
            Keccak[] hashes = rlpStream.DecodeArray(item => item.DecodeKeccak());
            return new NewPooledTransactionHashesMessage68(types, sizes, hashes);
        }

        public void Serialize(IByteBuffer byteBuffer, NewPooledTransactionHashesMessage68 message)
        {
            int typesSize = Rlp.LengthOfSequence(message.Types.Count);
            int sizesSize = Rlp.LengthOfSequence(message.Sizes.Aggregate(0, (i, u) => i + Rlp.LengthOf(u)));
            int hashesSize = Rlp.LengthOfSequence(message.Hashes.Aggregate(0, (i, keccak) => i + Rlp.LengthOf(keccak)));

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(hashesSize + sizesSize + typesSize));

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(typesSize);
            foreach (TxType type in message.Types)
            {
                rlpStream.Encode((byte)type);
            }

            rlpStream.StartSequence(sizesSize);
            foreach (uint size in message.Sizes)
            {
                rlpStream.Encode(size);
            }

            rlpStream.StartSequence(hashesSize);
            foreach (Keccak hash in message.Hashes)
            {
                rlpStream.Encode(hash);
            }
        }
    }
}
