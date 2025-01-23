// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public class PeerEventArgs : EventArgs
{
    public PeerEventArgs(Node remoteNode)
    {
        node = remoteNode;
    }
    public PeerEventArgs(Node remoteNode, string msgProtocol, int msgPacketType, int msgSize)
    {
        node = remoteNode;
        messageInfo = new MessageInfo(msgProtocol, msgPacketType, msgSize);
    }

    public Node node { get; set; }
    public MessageInfo messageInfo { get; set; }

    public class MessageInfo
    {
        public MessageInfo(string msgProtocol, int msgPacketType, int msgSize)
        {
            protocol = msgProtocol;
            packetType = msgPacketType;
            size = msgSize;
        }
        public string protocol { get; set; }
        public int packetType { get; set; }
        public int size { get; set; }
    }
}
