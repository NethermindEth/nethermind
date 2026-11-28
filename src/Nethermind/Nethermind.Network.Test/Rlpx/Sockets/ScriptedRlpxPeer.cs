// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;

namespace Nethermind.Network.Test.Rlpx.Sockets;

/// <summary>
/// The far end of a connection under test: it writes the frames it is told to, valid or not, and reads what the
/// connection sends.
/// </summary>
/// <remarks>It holds the paired secrets, so its frames decode on the connection and the connection's on it.</remarks>
internal sealed class ScriptedRlpxPeer
{
    private readonly FrameEncoder _encoder;
    private readonly FrameDecoder _decoder;
    private readonly FrameMerger _merger = new();
    private readonly FrameCipher _egressCipher;
    private readonly FrameMacProcessor _egressMac;

    public ScriptedRlpxPeer(InMemoryRlpxSocket socket, EncryptionSecrets secrets)
    {
        Socket = socket;
        FrameMacProcessor ingressMac = new(secrets);
        _egressMac = new FrameMacProcessor(secrets);
        _egressCipher = new FrameCipher(secrets.AesSecret);
        _encoder = new FrameEncoder(_egressCipher, _egressMac);
        _encoder.EnableSnappy(LimboLogs.Instance.GetClassLogger<ScriptedRlpxPeer>());
        _decoder = new FrameDecoder(new FrameCipher(secrets.AesSecret), ingressMac, int.MaxValue);
    }

    public InMemoryRlpxSocket Socket { get; }

    /// <summary>Encodes a packet into frames, without sending them.</summary>
    /// <param name="packetType">A packet type below 0x80, which RLP encodes in one byte.</param>
    public byte[] Encode(byte packetType, ReadOnlySpan<byte> content, FrameEncoder.EncodingMode mode)
    {
        byte[] packet = new byte[1 + content.Length];
        // RLP encodes the integer zero as the empty string.
        packet[0] = packetType == 0 ? (byte)0x80 : packetType;
        content.CopyTo(packet.AsSpan(1));
        byte[] frames = new byte[GetMaxEncodedLength(packet.Length, mode)];
        return frames[..EncodePacket(packet, frames, mode)];
    }

    /// <summary>Gets an upper bound of the encoded size of a packet, its packet type included.</summary>
    public int GetMaxEncodedLength(int packetLength, FrameEncoder.EncodingMode mode) => _encoder.GetMaxEncodedLength(packetLength, mode);

    /// <summary>Encodes a packet, its packet type first, into frames in <paramref name="destination"/>, without allocating.</summary>
    /// <returns>The number of bytes written.</returns>
    public int EncodePacket(ReadOnlySpan<byte> packet, Span<byte> destination, FrameEncoder.EncodingMode mode) => _encoder.Encode(packet, destination, mode);

    /// <summary>Encrypts and authenticates a frame from a plaintext header and a padded payload, without checking either.</summary>
    public byte[] EncryptFrame(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[Frame.HeaderSize + Frame.MacSize + payload.Length + Frame.MacSize];
        Span<byte> encryptedHeader = frame.AsSpan(0, Frame.HeaderSize);
        _egressCipher.Encrypt(header, encryptedHeader);
        _egressMac.AddMac(encryptedHeader, frame.AsSpan(Frame.HeaderSize, Frame.MacSize), true);
        Span<byte> encryptedPayload = frame.AsSpan(Frame.HeaderSize + Frame.MacSize, payload.Length);
        _egressCipher.Encrypt(payload, encryptedPayload);
        _egressMac.AddMac(encryptedPayload, frame.AsSpan(Frame.HeaderSize + Frame.MacSize + payload.Length, Frame.MacSize), false);
        return frame;
    }

    public Task SendAsync(byte[] data) => Socket.SendAllAsync(data);

    /// <summary>Reads a handshake message, with its 2-byte size prefix.</summary>
    public async Task<byte[]> ReceiveHandshakeAsync(CancellationToken cancellationToken)
    {
        byte[] prefix = await Socket.ReceiveExactlyAsync(2, cancellationToken);
        byte[] body = await Socket.ReceiveExactlyAsync(prefix[0] << 8 | prefix[1], cancellationToken);
        return [.. prefix, .. body];
    }

    /// <summary>Reads, authenticates and merges frames until a packet is complete.</summary>
    /// <returns>The packet type and the content as sent, so compressed if it was sent with Snappy.</returns>
    public async Task<(byte PacketType, byte[] Content)> ReceivePacketAsync(CancellationToken cancellationToken)
    {
        List<byte>? chunks = null;
        byte packetType = 0;
        while (true)
        {
            byte[] header = await Socket.ReceiveExactlyAsync(Frame.HeaderSize + Frame.MacSize, cancellationToken);
            byte[] frame = new byte[Frame.HeaderSize];
            int payloadSize = _decoder.ReadHeader(header.AsSpan(0, Frame.HeaderSize), header.AsSpan(Frame.HeaderSize), frame);
            byte[] payload = await Socket.ReceiveExactlyAsync(payloadSize + Frame.MacSize, cancellationToken);
            _decoder.AbsorbPayload(payload.AsSpan(0, payloadSize));
            _decoder.CheckPayloadMac(payload.AsSpan(payloadSize));
            Array.Resize(ref frame, Frame.HeaderSize + payloadSize);
            _decoder.DecryptPayload(payload.AsSpan(0, payloadSize), frame.AsSpan(Frame.HeaderSize));

            MergedFrame merged = _merger.Read(frame);
            ReadOnlySpan<byte> content = frame.AsSpan(merged.ContentOffset, merged.ContentLength);
            if (merged.Kind == MergedFrameKind.SinglePacket)
            {
                return (merged.PacketType, content.ToArray());
            }

            if (merged.Kind == MergedFrameKind.FirstChunk)
            {
                packetType = merged.PacketType;
                chunks = [];
            }

            chunks!.AddRange(content);
            if (merged.IsComplete)
            {
                return (packetType, [.. chunks]);
            }
        }
    }
}

/// <summary>
/// A handshake that exchanges fixed messages and hands out given secrets, so a test can pair the connection with a
/// <see cref="ScriptedRlpxPeer"/>.
/// </summary>
internal sealed class FakeHandshakeService(EncryptionSecrets secrets, PublicKey remoteNodeId) : IHandshakeService
{
    /// <summary>Gets a handshake message: a 2-byte size prefix and a 3-byte body.</summary>
    public static byte[] Message => [0, 3, 1, 2, 3];

    /// <summary>Gets the AUTH or ACK messages the handshake was given, as received.</summary>
    public List<byte[]> Received { get; } = [];

    public Packet Auth(PublicKey remoteNodeId, EncryptionHandshake handshake, bool preEip8Format = false) => new(Message);

    public Packet Ack(EncryptionHandshake handshake, Packet auth)
    {
        Received.Add(auth.Data);
        Complete(handshake);
        return new Packet(Message);
    }

    public void Agree(EncryptionHandshake handshake, Packet ack)
    {
        Received.Add(ack.Data);
        Complete(handshake);
    }

    private void Complete(EncryptionHandshake handshake)
    {
        handshake.Secrets = secrets;
        handshake.RemoteNodeId = remoteNodeId;
    }
}
