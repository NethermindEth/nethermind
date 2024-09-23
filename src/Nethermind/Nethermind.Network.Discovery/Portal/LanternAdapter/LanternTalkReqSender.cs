// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Table;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Portal.LanternAdapter;

/// <summary>
/// Translate whatever is in Lantern into an ITalkReqTransport.
/// </summary>
/// <param name="routingTable"></param>
/// <param name="packetManager"></param>
/// <param name="messageDecoder"></param>
/// <param name="logManager"></param>
public class LanternTalkReqSender(
    IRoutingTable routingTable,
    IPacketManager packetManager,
    IMessageDecoder messageDecoder,
    ILogManager logManager
) : IRawTalkReqSender
{
    private ILogger _logger = logManager.GetClassLogger<LanternTalkReqSender>();

    public async Task<TalkReqMessage> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
    {
        // Needed as its possible that the routing table does not
        routingTable.UpdateFromEnr(receiver);

        var destIpKey = receiver.GetEntry<EntryIp>(EnrEntryKey.Ip).Value;
        var nodeId = receiver.NodeId.ToHexString();

        byte[]? sentMessage = null;
        // So sentMessage can be null if there is an ongoing handshake that is not completed yet.
        // So what happen here is that, if the receiver does not have any session yet, lantern will create a "cached"
        // packet which will be used as a reply to a WHOAREYOU message.
        // BUT if the are already a "cached" request, lantern will straight up fail to send a message and return null. (TODO: double check this)
        do
        {
            token.ThrowIfCancellationRequested();
            sentMessage = (await packetManager.SendPacket(receiver, MessageType.TalkReq, false, protocol, message));
            if (sentMessage == null)
            {
                // Well.... got another idea?
                await Task.Delay(100, token);
            }
        } while (sentMessage == null);

        // Yea... it does not return the original message, so we have to decode it to get the request id.
        // Either this or we have some more hacks.
        var talkReqMessage = (TalkReqMessage)messageDecoder.DecodeMessage(sentMessage);
        if (_logger.IsDebug) _logger.Debug($"Sent TalkReq to {destIpKey}, {nodeId} with request id {talkReqMessage.RequestId.ToHexString()}");
        return talkReqMessage;
    }
}
