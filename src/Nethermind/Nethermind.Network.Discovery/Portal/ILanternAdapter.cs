// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public interface ILanternAdapter
{
    Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage message);
    void OnMsgResp(IEnr sender, TalkRespMessage message);
    IMessageSender<IEnr, byte[]> CreateMessageSenderForProtocol(byte[] protocol);
    void RegisterKademliaOverlay(byte[] protocol, IKademlia<IEnr, byte[]> kademlia);
}
