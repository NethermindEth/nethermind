using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Packet.Handlers;

public class HandshakePacketHandler(IIdentityManager identityManager,
        ISessionManager sessionManager,
        IRoutingTable routingTable,
        IMessageResponder messageResponder,
        IUdpConnection udpConnection,
        IPacketBuilder packetBuilder,
        IPacketProcessor packetProcessor,
        IEnrFactory enrFactory,
        ILoggerFactory loggerFactory)
    : PacketHandlerBase
{
    private readonly ILogger<HandshakePacketHandler> _logger = loggerFactory.CreateLogger<HandshakePacketHandler>();

    public override PacketType PacketType => PacketType.Handshake;

    public override async Task HandlePacket(UdpReceiveResult returnedResult)
    {
        _logger.LogInformation("Received HANDSHAKE packet from {RemoteEndPoint}", returnedResult.RemoteEndPoint);
        var packet = returnedResult.Buffer;


        if (!packetProcessor.TryGetStaticHeader(packet, out StaticHeader? staticHeader))
        {
            _logger.LogDebug("Cannot decode static header of HANDSHAKE packet");
            return;
        }

        var handshakePacket = HandshakePacketBase.CreateFromStaticHeader(staticHeader);
        var publicKey = ObtainPublicKey(handshakePacket, handshakePacket.SrcId!);

        if (publicKey == null)
        {
            _logger.LogWarning("Cannot obtain public key from record. Unable to verify ID signature from HANDSHAKE packet");
            return;
        }

        var session = sessionManager.GetSession(handshakePacket.SrcId!, returnedResult.RemoteEndPoint);

        if (session == null)
        {
            _logger.LogWarning("Session not found. Cannot verify ID signature from HANDSHAKE packet");
            return;
        }

        var idSignatureVerificationResult = session.VerifyIdSignature(handshakePacket, publicKey, identityManager.Record.NodeId);

        if (!idSignatureVerificationResult)
        {
            _logger.LogError("ID signature verification failed. Cannot decrypt message in the HANDSHAKE packet");
            return;
        }

        if (!idSignatureVerificationResult)
        {
            _logger.LogError("ID signature verification failed. Cannot decrypt message in the HANDSHAKE packet");
            return;
        }

        byte[]? encryptedMessage = packetProcessor.GetEncryptedMessage(packet);

        if (encryptedMessage is null)
        {
            _logger.LogError("ID signature verification failed. Cannot decrypt message in the HANDSHAKE packet");
            return;
        }

        var decryptedMessage = session.DecryptMessageWithNewKeys(staticHeader, packetProcessor.GetMaskingIv(packet), encryptedMessage, handshakePacket, identityManager.Record.NodeId);

        if (decryptedMessage == null)
        {
            _logger.LogWarning("Cannot decrypt message in the HANDSHAKE packet");
            return;
        }

        _logger.LogDebug("Successfully decrypted HANDSHAKE packet");

        var replies = await PrepareMessageForHandshake(decryptedMessage, handshakePacket.SrcId!, session, returnedResult.RemoteEndPoint);

        if (replies == null || replies.Length == 0)
            return;

        foreach (var reply in replies)
        {
            await udpConnection.SendAsync(reply, returnedResult.RemoteEndPoint);
            _logger.LogInformation("Sent response to HANDSHAKE packet");
        }
    }

    private byte[]? ObtainPublicKey(HandshakePacketBase handshakePacketBase, byte[]? senderNodeId)
    {
        IEnr? senderRecord = null;

        if (handshakePacketBase.Record?.Length > 0)
        {
            senderRecord = enrFactory.CreateFromBytes(handshakePacketBase.Record, identityManager.Verifier);
        }
        else if (senderNodeId != null)
        {
            var nodeEntry = routingTable.GetNodeEntryForNodeId(senderNodeId);

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

        routingTable.UpdateFromEnr(senderRecord);

        return senderRecord.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
    }

    private async Task<byte[][]?> PrepareMessageForHandshake(byte[] decryptedMessage, byte[] senderNodeId, ISessionMain sessionMain, IPEndPoint endPoint)
    {
        var responses = await messageResponder.HandleMessageAsync(decryptedMessage, endPoint);

        if (responses == null || responses.Length == 0)
        {
            return null;
        }

        var responsesList = new List<byte[]>();

        foreach (var response in responses)
        {
            var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
            var ordinaryPacket = packetBuilder.BuildOrdinaryPacket(response, senderNodeId, maskingIv, sessionMain.MessageCount);
            var encryptedMessage = sessionMain.EncryptMessage(ordinaryPacket.Header, maskingIv, response);
            responsesList.Add(ByteArrayUtils.JoinByteArrays(ordinaryPacket.Packet, encryptedMessage));
        }

        return responsesList.ToArray();
    }
}
