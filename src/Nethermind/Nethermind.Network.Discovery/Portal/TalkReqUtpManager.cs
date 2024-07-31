// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.UTP;
using NonBlocking;

namespace Nethermind.Network.Discovery.Portal;

public class TalkReqUtpManager: IUtpManager, ITalkReqProtocolHandler
{
    private static readonly byte[] UtpProtocolByte = Bytes.FromHexString("0x757470");
    public int MaxContentByteSize => 1000;

    private readonly ITalkReqTransport _talkReqTransport;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    // TODO: Maybe use Lru?
    private readonly ConcurrentDictionary<(IEnr, ushort), UTPStream> _utpStreams = new();

    public TalkReqUtpManager(ITalkReqTransport transport, ILogManager logManager)
    {
        transport.RegisterProtocol(UtpProtocolByte, this);
        _talkReqTransport = transport;
        _logger = logManager.GetClassLogger<TalkReqUtpManager>();
        _logManager = logManager;
    }

    public ushort InitiateUtpStreamSender(IEnr sender, byte[] valuePayload)
    {
        throw new NotImplementedException();
    }


    public async Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage)
    {
        (UTPPacketHeader header, int headerSize) = UTPPacketHeader.DecodePacket(talkReqMessage.Request);
        _logger.Info($"Handle utp message from :{header.ConnectionId} {header}");
        if (!_utpStreams.TryGetValue((sender, header.ConnectionId), out UTPStream? stream))
        {
            _logger.Info($"Unknown connection id");
            return null;
        }

        await stream.ReceiveMessage(header, talkReqMessage.Request.AsSpan()[headerSize..], CancellationToken.None);
        return Array.Empty<byte>();
    }

    public async Task<byte[]?> DownloadContentFromUtp(IEnr nodeId, ushort connectionId, CancellationToken token)
    {
        byte[] asByte = new byte[2];

        // TODO: Ah man... where did this go wrong.
        BinaryPrimitives.WriteUInt16LittleEndian(asByte, connectionId);
        connectionId = BinaryPrimitives.ReadUInt16BigEndian(asByte);

        if (_logger.IsDebug) _logger.Debug($"Downloading UTP content from {nodeId} with connection id {connectionId}");

        // Now, UTP have this strange connection id mechanism where the initiator will initiate with starting connection id
        // BUT after the Syn, it send with that connection id + 1.
        ushort otherSideConnectionId = (ushort)(connectionId + 1);
        UTPStream stream = new UTPStream(new UTPToMsgReqAdapter(nodeId, _talkReqTransport, _logger), otherSideConnectionId, _logManager);
        if (!_utpStreams.TryAdd((nodeId, (ushort)(connectionId)), stream))
        {
            throw new Exception("Unable to open utp stream. Connection id may already be used.");
        }

        try
        {
            MemoryStream outputStream = new MemoryStream();
            await stream.InitiateHandshake(token, synConnectionId: connectionId);
            await stream.ReadStream(outputStream, token);
            return outputStream.ToArray();
        }
        finally
        {
            _utpStreams.Remove((nodeId, connectionId), out _);
        }
    }

    private class UTPToMsgReqAdapter(IEnr targetNode, ITalkReqTransport talkReqTransport, ILogger logger): IUTPTransfer
    {
        public Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
        {
            var dataArray = UTPPacketHeader.EncodePacket(meta, data, new byte[2047]).ToArray();
            logger.Info($"Sending utp message to {meta.ConnectionId} {meta}.");
            return talkReqTransport.SentTalkReq(targetNode, UtpProtocolByte, dataArray, token);
        }
    }


}

public interface IUtpManager
{
    ushort InitiateUtpStreamSender(IEnr sender, byte[] valuePayload);
    int MaxContentByteSize { get; }
    Task<byte[]?> DownloadContentFromUtp(IEnr node, ushort valueConnectionId, CancellationToken token);
}
