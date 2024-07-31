// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;

namespace Nethermind.Network.Discovery.Portal;

public interface ITalkReqTransport: ITalkReqProtocolHandler
{
    /// <summary>
    /// For calling from lantern. Used to implement CallAndWaitForResponse
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="message"></param>
    void OnTalkResp(IEnr sender, TalkRespMessage message);

    /// <summary>
    /// For registering protocol. Notably, this is how a protocol receive and handle TalkReq.
    /// </summary>
    /// <param name="protocol"></param>
    /// <param name="protocolHandler"></param>
    void RegisterProtocol(byte[] protocol, ITalkReqProtocolHandler protocolHandler);

    /// <summary>
    /// For sending TalkReq and waiting for its corresponding TalkResp
    /// </summary>
    /// <param name="receiver"></param>
    /// <param name="protocol"></param>
    /// <param name="message"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<byte[]> CallAndWaitForResponse(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token);

    /// <summary>
    /// For sending TalkReq without waiting for response.
    /// </summary>
    /// <param name="receiver"></param>
    /// <param name="protocol"></param>
    /// <param name="message"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<TalkReqMessage> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token);
}

public interface ITalkReqProtocolHandler
{
    /// <summary>
    /// For handling TalkReq. Very similar to lantern's ITalkReqAndRespHandler, except it passes in sender.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="talkReqMessage"></param>
    /// <returns></returns>
    Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage);
}
