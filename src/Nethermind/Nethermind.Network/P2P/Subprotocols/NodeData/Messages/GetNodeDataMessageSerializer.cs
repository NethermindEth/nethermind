// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;

namespace Nethermind.Network.P2P.Subprotocols.NodeData.Messages;

public class GetNodeDataMessageSerializer : HashesMessageSerializer<GetNodeDataMessage>
{
    public override GetNodeDataMessage Deserialize(IByteBuffer byteBuffer)
    {
        Hash256[] keys = DeserializeHashes(byteBuffer);
        return new GetNodeDataMessage(keys);
    }
}
