// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockBodiesMessageSerializer(BlockBodyDecoder blockBodyDecoder = null)
        : Eth66MessageSerializer<BlockBodiesMessage, V62.Messages.BlockBodiesMessage>(new V62.Messages.BlockBodiesMessageSerializer(blockBodyDecoder));
}
