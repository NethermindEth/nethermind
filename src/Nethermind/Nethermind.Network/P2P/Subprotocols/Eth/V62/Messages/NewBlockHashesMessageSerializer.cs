// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class NewBlockHashesMessageSerializer : IZeroInnerMessageSerializer<NewBlockHashesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, NewBlockHashesMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
            NettyRlpStream nettyRlpStream = new(byteBuffer);

            nettyRlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                int miniContentLength = Rlp.LengthOf(message.BlockHashes[i].Item1);
                miniContentLength += Rlp.LengthOf(message.BlockHashes[i].Item2);
                nettyRlpStream.StartSequence(miniContentLength);
                nettyRlpStream.Encode(message.BlockHashes[i].Item1);
                nettyRlpStream.Encode(message.BlockHashes[i].Item2);
            }
        }

        public NewBlockHashesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public int GetLength(NewBlockHashesMessage message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.BlockHashes.Length; i++)
            {
                int miniContentLength = Rlp.LengthOf(message.BlockHashes[i].Item1);
                miniContentLength += Rlp.LengthOf(message.BlockHashes[i].Item2);
                contentLength += Rlp.LengthOfSequence(miniContentLength);
            }

            return Rlp.LengthOfSequence(contentLength);
        }

        private static NewBlockHashesMessage Deserialize(RlpStream rlpStream)
        {
            (Keccak, long)[] blockHashes = rlpStream.DecodeArray(ctx =>
            {
                ctx.ReadSequenceLength();
                return (ctx.DecodeKeccak(), (long)ctx.DecodeUInt256());
            }, false);

            return new NewBlockHashesMessage(blockHashes);
        }
    }
}
