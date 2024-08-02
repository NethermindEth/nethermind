// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;

namespace Nethermind.Network.Discovery.Portal;

public interface IRawTalkReqSender
{
    /// <summary>
    /// For sending TalkReq without waiting for response.
    /// At the moment, can't find a way to send a talkreqmessage with a predefined message id with lantern.
    /// </summary>
    /// <param name="receiver"></param>
    /// <param name="protocol"></param>
    /// <param name="message"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<TalkReqMessage> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token);
}
