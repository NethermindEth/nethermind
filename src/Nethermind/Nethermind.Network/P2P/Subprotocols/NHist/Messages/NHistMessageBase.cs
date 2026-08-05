// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public abstract class NHistMessageBase : P2PMessage, IEth66Message
{
    public override string Protocol => Contract.P2P.Protocol.NHist;

    public long RequestId { get; set; } = MessageConstants.Random.NextLong();
}
