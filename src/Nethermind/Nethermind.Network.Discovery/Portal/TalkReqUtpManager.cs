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
        if (_logger.IsTrace) _logger.Trace($"Received utp message from :{header.ConnectionId} {header}");

        if (!_utpStreams.TryGetValue((sender, header.ConnectionId), out UTPStream? stream))
        {
            if (_logger.IsDebug) _logger.Debug($"Unknown connection id :{header.ConnectionId}. Resetting...");
            await SendReset(sender, header.ConnectionId);
            return null;
        }

        await stream.ReceiveMessage(header, talkReqMessage.Request.AsSpan()[headerSize..], CancellationToken.None);
        return Array.Empty<byte>();
    }

    private Task SendReset(IEnr receiver, ushort connectionId)
    {
        UTPPacketHeader resetHeader = new UTPPacketHeader()
        {
            PacketType = UTPPacketType.StReset,
            Version = 1,
            ConnectionId = connectionId,
            WindowSize = 1_000_000,
            SeqNumber = 0,
            AckNumber = 0,
            SelectiveAck = null,
            TimestampMicros = 1_000_000,
            TimestampDeltaMicros = 0
        };

        return SendUtpPacket(receiver, resetHeader, ReadOnlySpan<byte>.Empty, CancellationToken.None);
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
        UTPStream stream = new UTPStream(new UTPToMsgReqAdapter(nodeId, this), otherSideConnectionId, _logManager);
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

    private Task SendUtpPacket(IEnr targetNode, UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Sending utp message to {meta.ConnectionId} {meta}.");

        var dataArray = UTPPacketHeader.EncodePacket(meta, data, new byte[2047]).ToArray();
        return _talkReqTransport.SentTalkReq(targetNode, UtpProtocolByte, dataArray, token);
    }

    private class UTPToMsgReqAdapter(IEnr node, TalkReqUtpManager manager): IUTPTransfer
    {
        public Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
        {
            return manager.SendUtpPacket(node, meta, data, token);
        }
    }
}

public interface IUtpManager
{
    ushort InitiateUtpStreamSender(IEnr sender, byte[] valuePayload);
    int MaxContentByteSize { get; }
    Task<byte[]?> DownloadContentFromUtp(IEnr node, ushort valueConnectionId, CancellationToken token);
}
