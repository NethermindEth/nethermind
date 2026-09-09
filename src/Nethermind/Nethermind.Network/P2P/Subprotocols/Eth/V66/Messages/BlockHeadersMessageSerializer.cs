// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockHeadersMessageSerializer(IHeaderDecoder headerDecoder = null)
        : Eth66MessageSerializer<BlockHeadersMessage, V62.Messages.BlockHeadersMessage>(new V62.Messages.BlockHeadersMessageSerializer(headerDecoder));
}
