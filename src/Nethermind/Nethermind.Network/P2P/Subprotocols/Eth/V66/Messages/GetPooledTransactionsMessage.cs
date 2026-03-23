// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

public class GetPooledTransactionsMessage : V65.Messages.GetPooledTransactionsMessage, IEth66Message, INew<IOwnedReadOnlyList<Hash256>, GetPooledTransactionsMessage>
{
    public long RequestId { get; set; } = MessageConstants.Random.NextLong();

    public GetPooledTransactionsMessage(IOwnedReadOnlyList<Hash256> hashes)
        : this(MessageConstants.Random.NextLong(), hashes)
    {
    }

    public GetPooledTransactionsMessage(long requestId, IOwnedReadOnlyList<Hash256> hashes)
        : base(hashes)
    {
        RequestId = requestId;
    }

    public GetPooledTransactionsMessage(long requestId, V65.Messages.GetPooledTransactionsMessage message)
        : this(requestId, message.Hashes)
    {
    }

    public new static GetPooledTransactionsMessage New(IOwnedReadOnlyList<Hash256> arg) => new(arg);
}
