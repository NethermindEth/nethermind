// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Stats.Model;

namespace Nethermind.Network;

public class PeerEventArgs : EventArgs
{
    public PeerEventArgs(Peer peer)
    {
        Peer = peer;
        Node = peer.Node;
    }
    public PeerEventArgs(Node remoteNode, string msgProtocol, int msgPacketType, int msgSize)
    {
        Node = remoteNode;
        MessageInfo = new MessageInfoModel(msgProtocol, msgPacketType, msgSize);
    }

    public Peer Peer { get; set; }
    public Node Node { get; set; }
    public MessageInfoModel MessageInfo { get; set; }

    public class MessageInfoModel(string msgProtocol, int msgPacketType, int msgSize)
    {
        public string Protocol { get; set; } = msgProtocol;
        public int PacketType { get; set; } = msgPacketType;
        public int Size { get; set; } = msgSize;
    }
}
