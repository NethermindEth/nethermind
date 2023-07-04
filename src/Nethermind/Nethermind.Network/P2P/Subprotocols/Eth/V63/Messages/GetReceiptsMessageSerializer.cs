// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetReceiptsMessageSerializer : HashesMessageSerializer<GetReceiptsMessage>
    {
        public GetReceiptsMessage Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
            Keccak[] hashes = rlpStream.DecodeArray(itemContext => itemContext.DecodeKeccak());
            return new GetReceiptsMessage(hashes);
        }

        public override GetReceiptsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public static GetReceiptsMessage Deserialize(RlpStream rlpStream)
        {
            Keccak[] hashes = DeserializeHashes(rlpStream);
            return new GetReceiptsMessage(hashes);
        }
    }
}
