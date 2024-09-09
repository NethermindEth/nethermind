// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.UTP;
using NonBlocking;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Implement UTP handling based on TalkReq via ITalkReqTransport.
/// </summary>
public class TalkReqUtpManager: IUtpManager, ITalkReqProtocolHandler
{
    private static readonly byte[] UtpProtocolByte = Bytes.FromHexString("0x757470");

    private readonly ITalkReqTransport _talkReqTransport;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    // TODO: Maybe use Lru?
    private readonly ConcurrentDictionary<(ValueHash256, ushort), UTPStream> _utpStreams = new();

    public TalkReqUtpManager(ITalkReqTransport transport, ILogManager logManager)
    {
        transport.RegisterProtocol(UtpProtocolByte, this);
        _talkReqTransport = transport;
        _logger = logManager.GetClassLogger<TalkReqUtpManager>();
        _logManager = logManager;
    }

    public async Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage talkReqMessage)
    {
        (UTPPacketHeader header, int headerSize) = UTPPacketHeader.DecodePacket(talkReqMessage.Request);
        if (_logger.IsTrace) _logger.Trace($"Received utp message from :{header.ConnectionId} {header}");

        ushort connectionId = header.ConnectionId;
        if (header.PacketType == UTPPacketType.StSyn)
        {
            // Note, the StSyn is one, less than other connection id.
            connectionId++;
        }

        if (!_utpStreams.TryGetValue((new ValueHash256(sender.NodeId), connectionId), out UTPStream? stream))
        {
            if (_logger.IsDebug) _logger.Debug($"Unknown connection id :{connectionId}. Resetting...");
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

    public async Task WriteContentToUtp(IEnr nodeId, bool isInitiator, ushort connectionId, Stream input, CancellationToken token)
    {
        // TODO: Ah man... where did this go wrong.
        byte[] asByte = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(asByte, connectionId);
        connectionId = BinaryPrimitives.ReadUInt16BigEndian(asByte);

        if (_logger.IsDebug) _logger.Debug($"Listening to UTP upload request from {nodeId} with connection id {connectionId}. Is initiator {isInitiator}");

        ushort peerConnectionId = 0;
        ushort ourConnectionId = 0;

        if (isInitiator)
        {
            peerConnectionId = (ushort)(connectionId + 1);
            ourConnectionId = connectionId;
        }
        else
        {
            peerConnectionId = connectionId;
            ourConnectionId = (ushort)(connectionId + 1);
        }

        UTPStream stream = new UTPStream(new UTPToMsgReqAdapter(nodeId, this), peerConnectionId, _logManager);
        if (!_utpStreams.TryAdd((new ValueHash256(nodeId.NodeId), ourConnectionId), stream))
        {
            throw new Exception("Unable to open utp stream. Connection id may already be used.");
        }

        try
        {
            if (isInitiator)
            {
                await stream.InitiateHandshake(token, ourConnectionId);
            }
            else
            {
                await stream.HandleHandshake(token);
            }
            await stream.WriteStream(input, token);
        }
        finally
        {
            _utpStreams.Remove((new ValueHash256(nodeId.NodeId), peerConnectionId), out _);
        }
    }

    public async Task ReadContentFromUtp(IEnr nodeId, bool isInitiator, ushort connectionId, Stream output, CancellationToken token)
    {
        // TODO: Ah man... where did this go wrong.
        byte[] asByte = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(asByte, connectionId);
        connectionId = BinaryPrimitives.ReadUInt16BigEndian(asByte);

        if (_logger.IsDebug) _logger.Debug($"Downloading UTP content from {nodeId} with connection id {connectionId}. Is initiator {isInitiator}.");

        ushort peerConnectionId = 0;
        ushort ourConnectionId = 0;

        if (isInitiator)
        {
            peerConnectionId = (ushort)(connectionId + 1);
            ourConnectionId = connectionId;
        }
        else
        {
            peerConnectionId = connectionId;
            ourConnectionId = (ushort)(connectionId + 1);
        }

        UTPStream stream = new UTPStream(new UTPToMsgReqAdapter(nodeId, this), peerConnectionId, _logManager);
        if (!_utpStreams.TryAdd((new ValueHash256(nodeId.NodeId), ourConnectionId), stream))
        {
            throw new Exception("Unable to open utp stream. Connection id may already be used.");
        }

        try
        {
            if (isInitiator)
            {
                await stream.InitiateHandshake(token, synConnectionId: ourConnectionId);
            }
            else
            {
                await stream.HandleHandshake(token);
            }
            await stream.ReadStream(output, token);
        }
        finally
        {
            _utpStreams.Remove((new ValueHash256(nodeId.NodeId), peerConnectionId), out _);
        }
    }

    private Task SendUtpPacket(IEnr targetNode, UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (_logger.IsTrace) _logger.Trace($"Sending utp message to {meta.ConnectionId} {meta}.");

        var dataArray = UTPPacketHeader.EncodePacket(meta, data, new byte[2047]).ToArray();
        return _talkReqTransport.SendTalkReq(targetNode, UtpProtocolByte, dataArray, token);
    }

    private class UTPToMsgReqAdapter(IEnr node, TalkReqUtpManager manager): IUTPTransfer
    {
        public Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
        {
            return manager.SendUtpPacket(node, meta, data, token);
        }
    }
}
