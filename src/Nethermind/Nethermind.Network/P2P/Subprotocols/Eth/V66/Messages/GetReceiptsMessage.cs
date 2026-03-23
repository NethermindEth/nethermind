// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetReceiptsMessage : V63.Messages.GetReceiptsMessage, IEth66Message
    {
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        public GetReceiptsMessage(long requestId, IOwnedReadOnlyList<Hash256> hashes)
            : base(hashes)
        {
            RequestId = requestId;
        }

        public GetReceiptsMessage(long requestId, V63.Messages.GetReceiptsMessage message)
            : this(requestId, message.Hashes)
        {
        }
    }
}
