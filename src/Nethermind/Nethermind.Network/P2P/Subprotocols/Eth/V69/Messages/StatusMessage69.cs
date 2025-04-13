// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class StatusMessage69 : V62.Messages.StatusMessage
{
    public override UInt256 TotalDifficulty
    {
        get => 0;
        set { }
    }

    public override string ToString()
    {
        return $"{Protocol}.{ProtocolVersion} network: {NetworkId} | best: {BestHash?.ToShortString()} | genesis: {GenesisHash?.ToShortString()} | fork: {ForkId}";
    }
}
