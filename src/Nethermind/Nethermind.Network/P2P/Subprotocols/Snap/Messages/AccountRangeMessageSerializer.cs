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
// 

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class AccountRangeMessageSerializer : IZeroMessageSerializer<AccountRangeMessage>
    {
        private readonly AccountDecoder _decoder = new (true);

        public void Serialize(IByteBuffer byteBuffer, AccountRangeMessage message)
        {
            (int contentLength, int accountsLength) = GetLength(message);
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);
            NettyRlpStream stream = new (byteBuffer);
            stream.StartSequence(contentLength);
            
            stream.Encode(message.RequestId);
            if (message.Accounts == null || message.Accounts.Length == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(accountsLength);
                for (int i = 0; i < message.Accounts.Length; i++)
                {
                    _decoder.Encode(message.Accounts[i], stream);
                }
            }
            
            stream.Encode(message.Proofs);
        }

        public AccountRangeMessage Deserialize(IByteBuffer byteBuffer)
        {
            AccountRangeMessage message = new();
            NettyRlpStream rlpStream = new (byteBuffer);
            
            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();
            message.Accounts = Rlp.DecodeArray(rlpStream, _decoder);
            message.Proofs = rlpStream.DecodeArray(_ => rlpStream.DecodeKeccak());

            return message;
        }
        
        private (int contentLength, int accountsLength) GetLength(AccountRangeMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            int accountsLength = _decoder.GetLength(message.Accounts);
            contentLength += Rlp.LengthOfSequence(accountsLength);
            contentLength += Rlp.LengthOf(message.Proofs, true);

            return (contentLength, accountsLength);
        }



    }
}
