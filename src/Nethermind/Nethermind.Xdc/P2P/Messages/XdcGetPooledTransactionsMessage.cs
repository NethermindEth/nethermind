// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;

namespace Nethermind.Xdc.P2P.Messages;

/// <summary>
/// <see cref="GetPooledTransactionsMessage"/> on the code XDC relocated it to.
/// </summary>
public class XdcGetPooledTransactionsMessage(IOwnedReadOnlyList<Hash256> hashes)
    : GetPooledTransactionsMessage(hashes), INew<IOwnedReadOnlyList<Hash256>, XdcGetPooledTransactionsMessage>
{
    public override int PacketType => XdcMessageCode.GetPooledTransactions;

    public static new XdcGetPooledTransactionsMessage New(IOwnedReadOnlyList<Hash256> arg) => new(arg);

    public override string ToString() => $"{nameof(XdcGetPooledTransactionsMessage)}({Hashes?.Count})";
}
