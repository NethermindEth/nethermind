// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Messages;

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
    protected override MsgType? FromMsgTypeByte(byte b) => b switch
    {
        2 => MsgType.Pong,
        3 => MsgType.FindNode,
        4 => MsgType.Neighbors,
        5 => MsgType.Ping,
        _ => null
    };
}
