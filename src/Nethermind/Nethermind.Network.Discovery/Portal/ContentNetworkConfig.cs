// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Network.Discovery.Portal;

public class ContentNetworkConfig
{
    /// <summary>
    /// The protocol id for the content
    /// </summary>
    public byte[] ProtocolId { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Kademlia k. As in how many item per bucket.
    /// </summary>
    public int K { get; set; } = 16;

    /// <summary>
    /// Kademlia a. As in concurrency level in lookup.
    /// </summary>
    public int A { get; set; } = 3;

    /// <summary>
    /// Interval between bucket refreshes.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Bootnodes for the content network
    /// </summary>
    public IEnr[] BootNodes { get; set; } = Array.Empty<IEnr>();

    /// <summary>
    /// The radius of content that this peer will store.
    /// </summary>
    public UInt256 ContentRadius { get; set; } = new UInt256(Bytes.FromHexString("0x0100000000000000000000000000000000000000000000000000000000000000"));

    /// <summary>
    /// The radius of peer when we don't know what is its radius. We assume it always want to store.
    /// </summary>
    public UInt256 DefaultPeerRadius { get; set; } = UInt256.MaxValue;
};
