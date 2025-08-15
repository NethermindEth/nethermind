using System.Net;
using System.Net.Sockets;
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

public class WhoAreYouPacketHandler(IIdentityManager identityManager,
        ISessionManager sessionManager,
        IRoutingTable routingTable,
        IRequestManager requestManager,
        IUdpConnection udpConnection,
        IPacketBuilder packetBuilder,
        IPacketProcessor packetProcessor,
        ILoggerFactory loggerFactory)
    : PacketHandlerBase
{
    private readonly ILogger<WhoAreYouPacketHandler> _logger = loggerFactory.CreateLogger<WhoAreYouPacketHandler>();

    public override PacketType PacketType => PacketType.WhoAreYou;

    public override async Task HandlePacket(UdpReceiveResult returnedResult)
    {
        _logger.LogTrace("Received WHOAREYOU packet from {Address}", returnedResult.RemoteEndPoint.Address);

        var packet = returnedResult.Buffer;

        if (!packetProcessor.TryGetStaticHeader(packet, out StaticHeader? staticHeader))
        {
            _logger.LogDebug("Failed to get dest node id from packet nonce. Ignoring WHOAREYOU request");
            return;
        }

        var destNodeId = requestManager.GetCachedHandshakeInteraction(staticHeader.Nonce);

        if (destNodeId == null)
        {
            _logger.LogDebug("Failed to get dest node id from packet nonce. Ignoring WHOAREYOU request");
            return;
        }

        var nodeEntry = routingTable.GetNodeEntryForNodeId(destNodeId);

        if (nodeEntry == null)
        {
            _logger.LogDebug("Cannot get node entry from the ENR table at node id: {NodeId}", Convert.ToHexString(destNodeId));
            return;
        }

        var session = GenerateOrUpdateSession(staticHeader, packetProcessor.GetMaskingIv(packet), destNodeId, returnedResult.RemoteEndPoint);

        if (session == null)
        {
            return;
        }

        var message = CreateReplyMessage(destNodeId);

        if (message == null)
        {
            _logger.LogDebug("Received unknown WHOAREYOU packet. Ignoring attempt to initiate a handshake");
            return;
        }

        var idSignature = session.GenerateIdSignature(destNodeId);

        if (idSignature == null)
        {
            return;
        }

        var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
        var handshakePacket = packetBuilder.BuildHandshakePacket(idSignature, session.EphemeralPublicKey, destNodeId, maskingIv, session.MessageCount);
        var encryptedMessage = session.EncryptMessageWithNewKeys(nodeEntry.Record, handshakePacket.Header, identityManager.Record.NodeId, message, maskingIv);

        if (encryptedMessage == null)
        {
            _logger.LogDebug("Cannot encrypt message with new keys");
            return;
        }

        var finalPacket = ByteArrayUtils.JoinByteArrays(handshakePacket.Packet, encryptedMessage);

        await udpConnection.SendAsync(finalPacket, returnedResult.RemoteEndPoint);
        _logger.LogTrace("Sent HANDSHAKE packet to {RemoteEndPoint}", returnedResult.RemoteEndPoint);
    }

    private ISessionMain? GenerateOrUpdateSession(StaticHeader header, byte[] maskingIv, byte[] destNodeId, IPEndPoint destEndPoint)
    {
        var session = sessionManager.GetSession(destNodeId, destEndPoint);

        if (session == null)
        {
            _logger.LogDebug("Creating new session with node: {Node}", destEndPoint);
            session = sessionManager.CreateSession(SessionType.Initiator, destNodeId, destEndPoint);
        }

        if (session != null)
        {
            session.SetChallengeData(maskingIv, header.GetHeader());
            return session;
        }

        _logger.LogDebug("Failed to create or update session with node: {Node}", destEndPoint);
        return null;
    }

    private byte[]? CreateReplyMessage(byte[] destNodeId)
    {
        var cachedRequest = requestManager.GetCachedRequest(destNodeId);

        if (cachedRequest == null)
        {
            var existingRequest = requestManager.GetPendingRequestByNodeId(destNodeId);

            if (existingRequest == null)
            {
                _logger.LogDebug("No cached or pending request found for node {NodeId}", Convert.ToHexString(destNodeId));
                return null;
            }

            var newRequest = new PendingRequest(destNodeId, existingRequest.Message)
            {
                IsLookupRequest = existingRequest.IsLookupRequest
            };

            requestManager.AddPendingRequest(existingRequest.Message.RequestId, newRequest);

            return existingRequest.Message.EncodeMessage();
        }

        requestManager.MarkCachedRequestAsFulfilled(destNodeId);
        _logger.LogTrace("Creating message from cached request {MessageType}", cachedRequest.Message.MessageType);

        var pendingRequest = new PendingRequest(cachedRequest.NodeId, cachedRequest.Message)
        {
            IsLookupRequest = cachedRequest.IsLookupRequest
        };

        requestManager.AddPendingRequest(cachedRequest.Message.RequestId, pendingRequest);

        return cachedRequest.Message.EncodeMessage();
    }
}
