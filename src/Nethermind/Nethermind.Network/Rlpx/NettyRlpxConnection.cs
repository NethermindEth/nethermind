// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.Network.Rlpx;

/// <summary>
/// A connection over a DotNetty channel.
/// </summary>
internal sealed class NettyRlpxConnection(IChannel channel, ILogManager logManager) : IRlpxConnection
{
    public bool IsWritable => channel.IsWritable;

    public void EnableSnappy()
    {
        // since groups were used, we are on a different thread
        channel.Pipeline.Get<ZeroNettyP2PHandler>()?.EnableSnappy();
        ZeroPacketSplitter splitter = channel.Pipeline.Get<ZeroPacketSplitter>()
            ?? throw new InvalidOperationException("Cannot enable Snappy without the outbound ZeroPacketSplitter.");
        splitter.EnableSnappy(logManager);
    }

    public Task DisconnectAsync() => channel.DisconnectAsync();
}
