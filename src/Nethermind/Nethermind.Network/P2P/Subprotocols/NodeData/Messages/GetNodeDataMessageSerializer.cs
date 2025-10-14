// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.NodeData.Messages;

public class GetNodeDataMessageSerializer : HashesMessageSerializer<GetNodeDataMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<GetNodeDataMessage>(nameof(GetNodeDataMessage.Hashes), NethermindSyncLimits.MaxHashesFetch);

    public override GetNodeDataMessage Deserialize(IByteBuffer byteBuffer)
    {
        IOwnedReadOnlyList<Hash256> keys = DeserializeHashesArrayPool(byteBuffer, RlpLimit);
        return new GetNodeDataMessage(keys);
    }
}
