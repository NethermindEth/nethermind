// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Etha.Messages;

// [version: P, networkid: P, genesis: B_32, forkid, earliestBlock: P, latestBlock: P, latestBlockHash: B_32]
public class StatusMessage : P2PMessage
{
    public byte ProtocolVersion { get; set; }
    public UInt256 NetworkId { get; set; }
    public required Hash256 GenesisHash { get; set; }
    public ForkId ForkId { get; set; }
    public long EarliestBlock { get; set; }
    public long LatestBlock { get; set; }
    public required Hash256 LatestBlockHash { get; set; }

    public override int PacketType { get; } = EthaMessageCode.Status;
    public override string Protocol { get; } = "etha";
    public uint BlockBitmask { get; set; }  

    public override string ToString()
    {
        return
            $"{Protocol}.{ProtocolVersion} network: {NetworkId} | genesis: {GenesisHash?.ToShortString()} | fork: {ForkId} | " +
            $"earliest: {EarliestBlock} | latest: {LatestBlock} ({LatestBlockHash?.ToShortString()})"
            + $" | blockBitmask: {BlockBitmask}";
    }
}
