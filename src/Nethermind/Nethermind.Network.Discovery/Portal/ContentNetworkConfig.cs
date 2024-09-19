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
    public UInt256 ContentRadius { get; set; } = new UInt256(Bytes.FromHexString("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));

    /// <summary>
    /// The radius of peer when we don't know what is its radius. We assume it always want to store.
    /// </summary>
    public UInt256 DefaultPeerRadius { get; set; } = UInt256.MaxValue;

    /// <summary>
    /// Maximum size of content before needing to use UTP to transfer content.
    /// </summary>
    public int MaxContentSizeForTalkReq { get; set; } = 1165;

    /// <summary>
    /// Timeout for task that download offer and process offer
    /// </summary>
    public TimeSpan OfferAcceptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for task that open a utp connection connection and stream content to receiver
    /// </summary>
    public TimeSpan StreamSenderTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for task that send offer to a peer and write content to it
    /// </summary>
    public TimeSpan OfferAndSendContentTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for looking up content and downloading them
    /// </summary>
    public TimeSpan LookupContentHardTimeout { get; set; } = TimeSpan.FromSeconds(60);
};
