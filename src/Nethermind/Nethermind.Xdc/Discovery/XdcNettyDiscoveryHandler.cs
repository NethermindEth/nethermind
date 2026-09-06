// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Messages;

namespace Nethermind.Xdc.Discovery;

public class XdcNettyDiscoveryHandler(
    IDiscoveryMsgListener? discoveryManager,
    IChannel? channel,
    IMessageSerializationService? msgSerializationService,
    ITimestamper? timestamper,
    ILogManager? logManager,
    NodeFilter? inboundMessageFilter = null)
    : NettyDiscoveryHandler(discoveryManager, channel, msgSerializationService, timestamper, logManager, inboundMessageFilter)
{
    // XDC remapped the standard disc-v4 type bytes: byte 1 (standard Ping) is unused;
    // byte 5 is XDC's pingXDC. ENR (bytes 5/6 in standard geth) is not supported by XDC
    protected override MsgType? FromMsgTypeByte(byte b) => b switch
    {
        2 => MsgType.Pong,
        3 => MsgType.FindNode,
        4 => MsgType.Neighbors,
        5 => MsgType.Ping,
        _ => null
    };
}
