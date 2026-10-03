// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetReceiptsMessage(IOwnedReadOnlyList<Hash256> blockHashes) : HashesMessage(blockHashes)
    {
        public override int PacketType => Eth63MessageCode.GetReceipts;
        public override string Protocol => "eth";

        /// <summary>
        /// Per requested block, the most receipts a response may hold for it, negative when unknown; blocks past its end have no limit.
        /// </summary>
        /// <remarks>Local bookkeeping for checking the response; not sent to the peer. It borrows the caller's buffer, which outlives the request.</remarks>
        public ReadOnlyMemory<int> MaxReceiptsPerBlock { get; init; }

        /// <summary>
        /// The number of blocks requested, kept because the message, with its pooled hashes, is disposed once it is sent.
        /// </summary>
        public int RequestedBlocks { get; } = blockHashes?.Count ?? 0;
    }
}
