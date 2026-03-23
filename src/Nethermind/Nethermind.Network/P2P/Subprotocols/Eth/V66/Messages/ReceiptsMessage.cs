// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class ReceiptsMessage : V63.Messages.ReceiptsMessage, IEth66Message
    {
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        public ReceiptsMessage(long requestId, IOwnedReadOnlyList<TxReceipt[]> txReceipts)
            : base(txReceipts)
        {
            RequestId = requestId;
        }

        public ReceiptsMessage(long requestId, V63.Messages.ReceiptsMessage message)
            : this(requestId, message.TxReceipts)
        {
        }
    }
}
