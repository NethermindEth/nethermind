// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetNodeDataMessageSerializer : HashesMessageSerializer<GetNodeDataMessage>
    {
        public override GetNodeDataMessage Deserialize(IByteBuffer byteBuffer)
        {
            ArrayPoolList<Hash256>? keys = DeserializeHashesArrayPool(byteBuffer);
            return new GetNodeDataMessage(keys);
        }
    }
}
