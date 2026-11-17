// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockHeadersMessage : P2PMessage
    {
        public override int PacketType => Eth62MessageCode.BlockHeaders;
        public override string Protocol => "eth";

        public IOwnedReadOnlyList<BlockHeader>? BlockHeaders { get; set; }

        /// <summary>The headers already encoded, one after another, which the serializer writes as they are.</summary>
        /// <remarks>When set, it is sent in place of <see cref="BlockHeaders"/>.</remarks>
        public IOwnedReadOnlyList<byte>? EncodedHeaders { get; init; }

        public BlockHeadersMessage()
        {
        }

        public BlockHeadersMessage(IOwnedReadOnlyList<BlockHeader>? blockHeaders) => BlockHeaders = blockHeaders;

        public override void Dispose()
        {
            BlockHeaders?.Dispose();
            EncodedHeaders?.Dispose();
        }

        public override string ToString() =>
            $"{nameof(BlockHeadersMessage)}({BlockHeaders?.Count ?? (EncodedHeaders is null ? 0 : new RlpReader(EncodedHeaders.AsSpan()).CountItems())})";
    }
}
