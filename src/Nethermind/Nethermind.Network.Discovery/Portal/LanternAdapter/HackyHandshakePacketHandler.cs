// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
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

namespace Nethermind.Network.Discovery.Portal.LanternAdapter;

public class HackyHandshakePacketHandler : HandshakePacketHandler
{

    private readonly ILogger<HandshakePacketHandler> _logger;
    private readonly IIdentityManager _identityManager;
    private readonly ISessionManager _sessionManager;
    private readonly IRoutingTable _routingTable;
    private readonly IMessageResponder _messageResponder;
    private readonly IUdpConnection _udpConnection;
    private readonly IPacketBuilder _packetBuilder;
    private readonly IPacketProcessor _packetProcessor;
    private readonly IMessageDecoder _messageDecoder;
    private readonly IRequestManager _requestManager;
    private readonly ITalkReqTransport _talkReqTransport;
    private readonly IEnrFactory _enrFactory;

    public HackyHandshakePacketHandler(IIdentityManager identityManager,
        ISessionManager sessionManager,
        IRoutingTable routingTable,
        IMessageResponder messageResponder,
        IUdpConnection udpConnection,
        IPacketBuilder packetBuilder,
        IPacketProcessor packetProcessor,
        IMessageDecoder messageDecoder,
        IRequestManager requestManager,
        ITalkReqTransport talkReqTransport,
        IEnrFactory enrFactory,
        ILoggerFactory loggerFactory) : base(identityManager, sessionManager, routingTable, messageResponder, udpConnection,
        packetBuilder, packetProcessor, enrFactory, loggerFactory)
    {
        _identityManager = identityManager;
        _sessionManager = sessionManager;
        _routingTable = routingTable;
        _messageResponder = messageResponder;
        _udpConnection = udpConnection;
        _packetBuilder = packetBuilder;
        _packetProcessor = packetProcessor;
        _messageDecoder = messageDecoder;
        _requestManager = requestManager;
        _talkReqTransport = talkReqTransport;
        _enrFactory = enrFactory;
        _logger = loggerFactory.CreateLogger<HandshakePacketHandler>();
    }

    public override async Task HandlePacket(UdpReceiveResult returnedResult)
    {
        _logger.LogInformation("Received HANDSHAKE packet from {RemoteEndPoint}", returnedResult.RemoteEndPoint);
        var packet = returnedResult.Buffer;
        var handshakePacket = HandshakePacketBase.CreateFromStaticHeader(_packetProcessor.GetStaticHeader(packet));
        var publicKey = ObtainPublicKey(handshakePacket, handshakePacket.SrcId!);
        _logger.LogInformation("Obtain pubkey {RemoteEndPoint}", returnedResult.RemoteEndPoint);

        if (publicKey == null)
        {
            _logger.LogWarning("Cannot obtain public key from record. Unable to verify ID signature from HANDSHAKE packet");
            return;
        }

        var session = _sessionManager.GetSession(handshakePacket.SrcId!, returnedResult.RemoteEndPoint);
        _logger.LogInformation("Get session {RemoteEndPoint}", returnedResult.RemoteEndPoint);

        if (session == null)
        {
            _logger.LogWarning("Session not found. Cannot verify ID signature from HANDSHAKE packet");
            return;
        }

        var idSignatureVerificationResult = session.VerifyIdSignature(handshakePacket, publicKey, _identityManager.Record.NodeId);
        _logger.LogInformation("Verify id signature {RemoteEndPoint}", returnedResult.RemoteEndPoint);

        if (!idSignatureVerificationResult)
        {
            _logger.LogError("ID signature verification failed. Cannot decrypt message in the HANDSHAKE packet");
            return;
        }

        var decryptedMessage = session.DecryptMessageWithNewKeys(_packetProcessor.GetStaticHeader(packet), _packetProcessor.GetMaskingIv(packet), _packetProcessor.GetEncryptedMessage(packet), handshakePacket, _identityManager.Record.NodeId);

        if (decryptedMessage == null)
        {
            _logger.LogWarning("Cannot decrypt message in the HANDSHAKE packet");
            return;
        }

        _logger.LogDebug("Successfully decrypted HANDSHAKE packet is it established? {established} the type {thetype}", session.IsEstablished, session.GetType().Name);

        // This is actually, the only special handling needed.
        var messageType = (MessageType)decryptedMessage[0];
        if (messageType is MessageType.TalkReq or MessageType.TalkResp)
        {
            var nodeEntry = _routingTable.GetNodeEntryForNodeId(handshakePacket.SrcId!)!;
            // _routingTable.MarkNodeAsLive(nodeEntry.Id);
            var reply = await HandleTalkReqMessage(nodeEntry.Record, decryptedMessage);
            if (reply != null)
            {
                await SendResponseToOrdinaryPacketAsync(nodeEntry.Id, session, returnedResult.RemoteEndPoint, _udpConnection, reply);
            }
        }

        var replies = await PrepareMessageForHandshake(decryptedMessage, handshakePacket.SrcId!, session, returnedResult.RemoteEndPoint);

        if (replies == null || replies.Length == 0)
            return;

        foreach (var reply in replies)
        {
            await _udpConnection.SendAsync(reply, returnedResult.RemoteEndPoint);
            _logger.LogInformation("Sent response to HANDSHAKE packet");
        }
    }

    private byte[]? ObtainPublicKey(HandshakePacketBase handshakePacketBase, byte[]? senderNodeId)
    {
        IEnr? senderRecord = null;

        if (handshakePacketBase.Record?.Length > 0)
        {
            senderRecord = _enrFactory.CreateFromBytes(handshakePacketBase.Record, _identityManager.Verifier);
        }
        else if (senderNodeId != null)
        {
            var nodeEntry = _routingTable.GetNodeEntryForNodeId(senderNodeId);

            if (nodeEntry != null)
            {
                senderRecord = nodeEntry.Record;
                return senderRecord.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
            }
        }

        if (senderRecord == null)
        {
            return null;
        }

        _routingTable.UpdateFromEnr(senderRecord);

        return senderRecord.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
    }

    private async Task<byte[][]?> PrepareMessageForHandshake(byte[] decryptedMessage, byte[] senderNodeId, ISessionMain sessionMain, IPEndPoint endPoint)
    {
        var responses = await _messageResponder.HandleMessageAsync(decryptedMessage, endPoint);

        if (responses == null || responses.Length == 0)
        {
            return null;
        }

        var responsesList = new List<byte[]>();

        foreach (var response in responses)
        {
            var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
            var ordinaryPacket = _packetBuilder.BuildOrdinaryPacket(response, senderNodeId, maskingIv, sessionMain.MessageCount);
            var encryptedMessage = sessionMain.EncryptMessage(ordinaryPacket.Header, maskingIv, response);
            responsesList.Add(ByteArrayUtils.JoinByteArrays(ordinaryPacket.Packet, encryptedMessage));
        }

        return responsesList.ToArray();
    }


    private async Task<byte[]?> HandleTalkReqMessage(IEnr enr, byte[] message)
    {
        _logger.LogInformation("Handling TalkReq from {enr}", MessageType.TalkReq);
        try
        {
            var decodedMessage = _messageDecoder.DecodeMessage(message);
            if (decodedMessage is TalkReqMessage talkReqMessage)
            {
                byte[]? resp = await _talkReqTransport.OnTalkReq(enr, talkReqMessage);
                if (resp != null)
                {
                    resp = new TalkRespMessage(talkReqMessage.RequestId, resp!).EncodeMessage();
                }
                return resp;
            }
            else
            {
                var talkResp = (TalkRespMessage) decodedMessage;
                _talkReqTransport.OnTalkResp(enr, talkResp);

                // So that it wont disconnect the peer
                // But this is a handshake. Would it be a resp? Should it always be a req?
                _requestManager.MarkRequestAsFulfilled(decodedMessage.RequestId);
                _requestManager.MarkCachedRequestAsFulfilled(decodedMessage.RequestId);

                return null;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning("Handling TalkReq from {enr} failed. {error}", MessageType.TalkReq, e);
            throw;
        }
    }
    private async Task SendResponseToOrdinaryPacketAsync(byte[] nodeId, ISessionMain sessionMain, IPEndPoint destEndPoint, IUdpConnection connection, byte[] response)
    {
        var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
        var ordinaryPacket = _packetBuilder.BuildOrdinaryPacket(response, nodeId, maskingIv, sessionMain.MessageCount);
        var encryptedMessage = sessionMain.EncryptMessage(ordinaryPacket.Header, maskingIv, response);
        var finalPacket = ByteArrayUtils.JoinByteArrays(ordinaryPacket.Packet, encryptedMessage);

        await connection.SendAsync(finalPacket, destEndPoint);
        _logger.LogInformation("Sent response to ORDINARY packet to {RemoteEndPoint}", destEndPoint);
    }
}

