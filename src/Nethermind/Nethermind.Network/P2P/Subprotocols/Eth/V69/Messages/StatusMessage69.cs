// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessage69 : V62.Messages.StatusMessage
{
    public StatusMessage69() { }

    public StatusMessage69(V62.Messages.StatusMessage message) : base(message) { }

    public override string ToString()
    {
        return $"{Protocol}.{ProtocolVersion} network: {NetworkId} | best: {BestHash?.ToShortString()} | genesis: {GenesisHash?.ToShortString()} | fork: {ForkId}";
    }
}
