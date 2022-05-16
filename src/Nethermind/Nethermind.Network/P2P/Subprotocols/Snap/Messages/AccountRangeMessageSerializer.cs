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
using Nethermind.Core.Extensions;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class AccountRangeMessageSerializer : IZeroMessageSerializer<AccountRangeMessage>
    {
        private readonly AccountDecoder _decoder = new (true);

        public void Serialize(IByteBuffer byteBuffer, AccountRangeMessage message)
        {
            (int contentLength, int pwasLength, int proofsLength) = GetLength(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);

            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);

            stream.Encode(message.RequestId);
            if (message.PathsWithAccounts == null || message.PathsWithAccounts.Length == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(pwasLength);
                for (int i = 0; i < message.PathsWithAccounts.Length; i++)
                {
                    PathWithAccount pwa = message.PathsWithAccounts[i];

                    int accountContentLength = _decoder.GetContentLength(pwa.Account);
                    int pwaLength = Rlp.LengthOf(pwa.Path) + Rlp.LengthOfSequence(accountContentLength);

                    stream.StartSequence(pwaLength);
                    stream.Encode(pwa.Path);
                    _decoder.Encode(pwa.Account, stream, accountContentLength);
                }
            }

            if (message.Proofs == null || message.Proofs.Length == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(proofsLength);
                for (int i = 0; i < message.Proofs.Length; i++)
                {
                    stream.Encode(message.Proofs[i]);
                }
            }
        }

        public AccountRangeMessage Deserialize(IByteBuffer byteBuffer)
        {
            AccountRangeMessage message = new();
            NettyRlpStream rlpStream = new (byteBuffer);

            rlpStream.ReadSequenceLength();

            message.RequestId = rlpStream.DecodeLong();
            message.PathsWithAccounts = rlpStream.DecodeArray(DecodePathWithRlpData);
            message.Proofs = rlpStream.DecodeArray(s => s.DecodeByteArray());

            return message;
        }
        
        private PathWithAccount DecodePathWithRlpData(RlpStream stream)
        {
            stream.ReadSequenceLength();

            PathWithAccount data = new(stream.DecodeKeccak(), _decoder.Decode(stream));

            return data;
        }

        private (int contentLength, int pwasLength, int proofsLength) GetLength(AccountRangeMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);

            int pwasLength = 0;
            if (message.PathsWithAccounts == null || message.PathsWithAccounts.Length == 0)
            {
                pwasLength = 1;
            }
            else
            {
                for (int i = 0; i < message.PathsWithAccounts.Length; i++)
                {
                    PathWithAccount pwa = message.PathsWithAccounts[i];
                    int itemLength = Rlp.LengthOf(pwa.Path);
                    itemLength += _decoder.GetLength(pwa.Account);

                    pwasLength += Rlp.LengthOfSequence(itemLength);
                }
            }

            contentLength += Rlp.LengthOfSequence(pwasLength);

            int proofsLength = 0;
            if (message.Proofs == null || message.Proofs.Length == 0)
            {
                proofsLength = 1;
            }
            else
            {
                for (int i = 0; i < message.Proofs.Length; i++)
                {
                    proofsLength += Rlp.LengthOf(message.Proofs[i]);
                }
            }

            contentLength += Rlp.LengthOfSequence(proofsLength);

            return (contentLength, pwasLength, proofsLength);
        }
    }
}
