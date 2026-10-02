// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetReceiptsMessage(IOwnedReadOnlyList<Hash256> blockHashes) : HashesMessage(blockHashes)
    {
        public override int PacketType => Eth63MessageCode.GetReceipts;
        public override string Protocol => "eth";

        /// <summary>
        /// Per requested block, the most receipts a response may hold for it, negative when unknown; <c>null</c> when no limit is known.
        /// </summary>
        /// <remarks>Local bookkeeping for checking the response; not sent to the peer.</remarks>
        public int[]? MaxReceiptsPerBlock { get; init; }

        /// <summary>
        /// The number of blocks requested, kept because the message, with its pooled hashes, is disposed once it is sent.
        /// </summary>
        public int RequestedBlocks { get; } = blockHashes?.Count ?? 0;
    }
}
