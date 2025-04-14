// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

// [version: P, networkid: P, genesis: B_32, forkid, earliestBlock: P, latestBlock: P, latestBlockHash: B_32]
public class StatusMessage69 : P2PMessage
{
    public byte ProtocolVersion { get; set; }
    public UInt256 NetworkId { get; set; }
    public required Hash256 GenesisHash { get; set; }
    public ForkId ForkId { get; set; }
    public long EarliestBlock { get; set; }
    public long LatestBlock { get; set; }
    public required Hash256 LatestBlockHash { get; set; }

    public override int PacketType { get; } = Eth69MessageCode.Status;
    public override string Protocol { get; } = "eth";

    public override string ToString()
    {
        return
            $"{Protocol}.{ProtocolVersion} network: {NetworkId} | genesis: {GenesisHash?.ToShortString()} | fork: {ForkId} | " +
            $"earliest: {EarliestBlock} | latest: {LatestBlock} ({LatestBlockHash?.ToShortString()})";
    }
}
