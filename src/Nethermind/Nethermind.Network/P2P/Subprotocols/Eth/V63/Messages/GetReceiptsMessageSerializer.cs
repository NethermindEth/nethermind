// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetReceiptsMessageSerializer : HashesMessageSerializer<GetReceiptsMessage>
    {
        public static GetReceiptsMessage Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
            ArrayPoolList<Hash256>? hashes = rlpStream.DecodeArrayPoolList(itemContext => itemContext.DecodeKeccak());
            return new GetReceiptsMessage(hashes);
        }

        public override GetReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static GetReceiptsMessage Deserialize(RlpStream rlpStream)
        {
            ArrayPoolList<Hash256>? hashes = HashesMessageSerializer<GetReceiptsMessage>.DeserializeHashesArrayPool(rlpStream);
            return new GetReceiptsMessage(hashes);
        }
    }
}
