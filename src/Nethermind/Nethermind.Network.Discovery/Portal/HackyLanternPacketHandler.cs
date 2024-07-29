// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Packet.Handlers;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.Portal;

public class HacklyLanternPacketHandler : OrdinaryPacketHandler
{
    private readonly ILogger<OrdinaryPacketHandler> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly IRoutingTable _routingTable;
    private readonly IMessageResponder _messageResponder;
    private readonly IUdpConnection _udpConnection;
    private readonly IPacketBuilder _packetBuilder;
    private readonly IPacketProcessor _packetProcessor;
    private readonly IMessageDecoder _messageDecoder;
    private readonly ILanternAdapter _lanternAdapter;

    public HacklyLanternPacketHandler(ISessionManager sessionManager,
        IRoutingTable routingTable,
        IMessageResponder messageResponder,
        IUdpConnection udpConnection,
        IPacketBuilder packetBuilder,
        IPacketProcessor packetProcessor,
        IMessageDecoder messageDecoder,
        ILanternAdapter lanternAdapter,
        ILoggerFactory loggerFactory) : base(sessionManager, routingTable, messageResponder, udpConnection, packetBuilder, packetProcessor, loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<OrdinaryPacketHandler>();
        _sessionManager = sessionManager;
        _routingTable = routingTable;
        _messageResponder = messageResponder;
        _udpConnection = udpConnection;
        _packetBuilder = packetBuilder;
        _packetProcessor = packetProcessor;

        _messageDecoder = messageDecoder;
        _lanternAdapter = lanternAdapter;
    }

    public override PacketType PacketType => PacketType.Ordinary;

    public override async Task HandlePacket(UdpReceiveResult returnedResult)
    {
        _logger.LogInformation("HL Received ORDINARY packet from {Address}", returnedResult.RemoteEndPoint.Address);

        var staticHeader = _packetProcessor.GetStaticHeader(returnedResult.Buffer);
        var maskingIv = _packetProcessor.GetMaskingIv(returnedResult.Buffer);
        var encryptedMessage = _packetProcessor.GetEncryptedMessage(returnedResult.Buffer);
        var nodeEntry = _routingTable.GetNodeEntryForNodeId(staticHeader.AuthData);

        _logger.LogInformation("Received ORDINARY packet from {NodeId}", Convert.ToHexString(staticHeader.AuthData));

        if (nodeEntry == null)
        {
            _logger.LogInformation("Could not find record in the table for node {NodeId} with {IpAddress}", Convert.ToHexString(staticHeader.AuthData), returnedResult.RemoteEndPoint.Address);
            await SendWhoAreYouPacketWithoutEnrAsync(staticHeader, returnedResult.RemoteEndPoint, _udpConnection);
            return;
        }

        var session = _sessionManager.GetSession(staticHeader.AuthData, returnedResult.RemoteEndPoint);

        if (session == null)
        {
            _logger.LogInformation("Cannot decrypt ORDINARY packet. No session found, sending WHOAREYOU packet");
            await SendWhoAreYouPacketAsync(staticHeader, nodeEntry.Record, returnedResult.RemoteEndPoint, _udpConnection);
            return;
        }

        var decryptedMessage = session.DecryptMessage(staticHeader, maskingIv, encryptedMessage);

        if (decryptedMessage == null)
        {
            _logger.LogInformation("Cannot decrypt ORDINARY packet. Decryption failed, sending WHOAREYOU packet");
            await SendWhoAreYouPacketAsync(staticHeader, nodeEntry.Record, returnedResult.RemoteEndPoint, _udpConnection);
            return;
        }

        _logger.LogDebug("Successfully decrypted ORDINARY packet");

        var replies = await _messageResponder.HandleMessageAsync(decryptedMessage, returnedResult.RemoteEndPoint);
        if (replies != null && replies.Length != 0)
        {
            foreach (var reply in replies)
            {
                await SendResponseToOrdinaryPacketAsync(staticHeader, session, returnedResult.RemoteEndPoint, _udpConnection, reply);
            }
        }

        // This is actually, the only special handling needed.
        var messageType = (MessageType)decryptedMessage[0];
        if (messageType is MessageType.TalkReq or MessageType.TalkResp)
        {
            _routingTable.MarkNodeAsLive(nodeEntry.Id);
            var reply = await HandleTalkReqMessage(nodeEntry.Record, decryptedMessage);
            if (reply != null)
            {
                await SendResponseToOrdinaryPacketAsync(staticHeader, session, returnedResult.RemoteEndPoint, _udpConnection, reply);
            }
        }
    }

    private async Task<byte[]?> HandleTalkReqMessage(IEnr enr, byte[] message)
    {
        _logger.LogInformation("Handling TalkReq from {enr}", MessageType.TalkReq);
        try
        {
            var decodedMessage = _messageDecoder.DecodeMessage(message);
            if (decodedMessage is TalkReqMessage talkReqMessage)
            {
                return await _lanternAdapter.OnMsgReq(enr, talkReqMessage);
            }
            else
            {
                _lanternAdapter.OnMsgResp(enr, (TalkRespMessage)decodedMessage);
                return null;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning("Handling TalkReq from {enr} failed. {error}", MessageType.TalkReq, e);
            throw;
        }
    }

    private async Task SendWhoAreYouPacketWithoutEnrAsync(StaticHeader staticHeader, IPEndPoint destEndPoint, IUdpConnection connection)
    {
        var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
        var whoAreYouPacket = _packetBuilder.BuildWhoAreYouPacketWithoutEnr(staticHeader.AuthData, staticHeader.Nonce, maskingIv);
        var session = _sessionManager.CreateSession(SessionType.Recipient, staticHeader.AuthData, destEndPoint);

        session.SetChallengeData(maskingIv, whoAreYouPacket.Header.GetHeader());

        await connection.SendAsync(whoAreYouPacket.Packet, destEndPoint);
        _logger.LogInformation("Sent WHOAREYOU packet to {RemoteEndPoint}", destEndPoint);
    }

    private async Task SendWhoAreYouPacketAsync(StaticHeader staticHeader, IEnr destNode, IPEndPoint destEndPoint, IUdpConnection connection)
    {
        var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
        var constructedWhoAreYouPacket = _packetBuilder.BuildWhoAreYouPacket(staticHeader.AuthData, staticHeader.Nonce, destNode, maskingIv);
        var session = _sessionManager.CreateSession(SessionType.Recipient, staticHeader.AuthData, destEndPoint);

        session.SetChallengeData(maskingIv, constructedWhoAreYouPacket.Header.GetHeader());

        await connection.SendAsync(constructedWhoAreYouPacket.Packet, destEndPoint);
        _logger.LogInformation("Sent WHOAREYOU packet to {RemoteEndPoint}", destEndPoint);
    }

    private async Task SendResponseToOrdinaryPacketAsync(StaticHeader staticHeader, ISessionMain sessionMain, IPEndPoint destEndPoint, IUdpConnection connection, byte[] response)
    {
        var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
        var ordinaryPacket = _packetBuilder.BuildOrdinaryPacket(response, staticHeader.AuthData, maskingIv, sessionMain.MessageCount);
        var encryptedMessage = sessionMain.EncryptMessage(ordinaryPacket.Header, maskingIv, response);
        var finalPacket = ByteArrayUtils.JoinByteArrays(ordinaryPacket.Packet, encryptedMessage);

        await connection.SendAsync(finalPacket, destEndPoint);
        _logger.LogInformation("Sent response to ORDINARY packet to {RemoteEndPoint}", destEndPoint);
    }
}
