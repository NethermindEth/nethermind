using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Packet.Handlers;

public class OrdinaryPacketHandler(ISessionManager sessionManager,
        IRoutingTable routingTable,
        IMessageResponder messageResponder,
        IUdpConnection udpConnection,
        IPacketBuilder packetBuilder,
        IPacketProcessor packetProcessor,
        ILoggerFactory loggerFactory)
    : PacketHandlerBase
{
    private readonly ILogger<OrdinaryPacketHandler> _logger = loggerFactory.CreateLogger<OrdinaryPacketHandler>();

    public override PacketType PacketType => PacketType.Ordinary;

    public override async Task HandlePacket(UdpReceiveResult returnedResult)
    {
        _logger.LogInformation("Received ORDINARY packet from {Address}", returnedResult.RemoteEndPoint.Address);

        if (!packetProcessor.TryGetStaticHeader(returnedResult.Buffer, out StaticHeader? staticHeader))
        {
            _logger.LogInformation("Could not decode static header for a node with {IpAddress}", returnedResult.RemoteEndPoint.Address);
            return;
        }

        var maskingIv = packetProcessor.GetMaskingIv(returnedResult.Buffer);
        var encryptedMessage = packetProcessor.GetEncryptedMessage(returnedResult.Buffer);

        if (encryptedMessage is null)
        {
            _logger.LogInformation("Could not find record in the table for node {NodeId} with {IpAddress}", Convert.ToHexString(staticHeader.AuthData), returnedResult.RemoteEndPoint.Address);
            await SendWhoAreYouPacketWithoutEnrAsync(staticHeader, returnedResult.RemoteEndPoint, udpConnection);
            return;
        }

        var nodeEntry = routingTable.GetNodeEntryForNodeId(staticHeader.AuthData);

        _logger.LogInformation("Received ORDINARY packet from {NodeId}", Convert.ToHexString(staticHeader.AuthData));

        if (nodeEntry == null)
        {
            _logger.LogInformation("Could not find record in the table for node {NodeId} with {IpAddress}", Convert.ToHexString(staticHeader.AuthData), returnedResult.RemoteEndPoint.Address);
            await SendWhoAreYouPacketWithoutEnrAsync(staticHeader, returnedResult.RemoteEndPoint, udpConnection);
            return;
        }

        var session = sessionManager.GetSession(staticHeader.AuthData, returnedResult.RemoteEndPoint);

        if (session == null)
        {
            _logger.LogInformation("Cannot decrypt ORDINARY packet. No session found, sending WHOAREYOU packet");
            await SendWhoAreYouPacketAsync(staticHeader, nodeEntry.Record, returnedResult.RemoteEndPoint, udpConnection);
            return;
        }

        var decryptedMessage = session.DecryptMessage(staticHeader, maskingIv, encryptedMessage);

        if (decryptedMessage == null)
        {
            _logger.LogInformation("Cannot decrypt ORDINARY packet. Decryption failed, sending WHOAREYOU packet");
            await SendWhoAreYouPacketAsync(staticHeader, nodeEntry.Record, returnedResult.RemoteEndPoint, udpConnection);
            return;
        }

        _logger.LogDebug("Successfully decrypted ORDINARY packet");

        var replies = await messageResponder.HandleMessageAsync(decryptedMessage, returnedResult.RemoteEndPoint);

        if (replies != null && replies.Length != 0)
        {
            foreach (var reply in replies)
            {
                await SendResponseToOrdinaryPacketAsync(staticHeader, session, returnedResult.RemoteEndPoint, udpConnection, reply);
            }
        }
    }

    private async Task SendWhoAreYouPacketWithoutEnrAsync(StaticHeader staticHeader, IPEndPoint destEndPoint, IUdpConnection connection)
    {
        var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
        var whoAreYouPacket = packetBuilder.BuildWhoAreYouPacketWithoutEnr(staticHeader.AuthData, staticHeader.Nonce, maskingIv);
        var session = sessionManager.CreateSession(SessionType.Recipient, staticHeader.AuthData, destEndPoint);

        session.SetChallengeData(maskingIv, whoAreYouPacket.Header.GetHeader());

        await connection.SendAsync(whoAreYouPacket.Packet, destEndPoint);
        _logger.LogInformation("Sent WHOAREYOU packet to {RemoteEndPoint}", destEndPoint);
    }

    private async Task SendWhoAreYouPacketAsync(StaticHeader staticHeader, IEnr destNode, IPEndPoint destEndPoint, IUdpConnection connection)
    {
        var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
        var constructedWhoAreYouPacket = packetBuilder.BuildWhoAreYouPacket(staticHeader.AuthData, staticHeader.Nonce, destNode, maskingIv);
        var session = sessionManager.CreateSession(SessionType.Recipient, staticHeader.AuthData, destEndPoint);

        session.SetChallengeData(maskingIv, constructedWhoAreYouPacket.Header.GetHeader());

        await connection.SendAsync(constructedWhoAreYouPacket.Packet, destEndPoint);
        _logger.LogInformation("Sent WHOAREYOU packet to {RemoteEndPoint}", destEndPoint);
    }

    private async Task SendResponseToOrdinaryPacketAsync(StaticHeader staticHeader, ISessionMain sessionMain, IPEndPoint destEndPoint, IUdpConnection connection, byte[] response)
    {
        var maskingIv = RandomUtility.GenerateRandomData(PacketConstants.MaskingIvSize);
        var ordinaryPacket = packetBuilder.BuildOrdinaryPacket(response, staticHeader.AuthData, maskingIv, sessionMain.MessageCount);
        var encryptedMessage = sessionMain.EncryptMessage(ordinaryPacket.Header, maskingIv, response);
        var finalPacket = ByteArrayUtils.JoinByteArrays(ordinaryPacket.Packet, encryptedMessage);

        await connection.SendAsync(finalPacket, destEndPoint);
        _logger.LogInformation("Sent response to ORDINARY packet to {RemoteEndPoint}", destEndPoint);
    }
}
