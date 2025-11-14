// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

public class GetPooledTransactionsMessage : Eth66Message<V65.Messages.GetPooledTransactionsMessage>, INew<IOwnedReadOnlyList<Hash256>, GetPooledTransactionsMessage>
{
    public GetPooledTransactionsMessage()
    {
    }

    public GetPooledTransactionsMessage(IOwnedReadOnlyList<Hash256> hashes) : base(MessageConstants.Random.NextLong(), new V65.Messages.GetPooledTransactionsMessage(hashes))
    {
    }

    public static GetPooledTransactionsMessage New(IOwnedReadOnlyList<Hash256> arg) => new(arg);
}
