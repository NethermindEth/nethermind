// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Discv5.Packets;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

internal readonly record struct SessionKey(ValueHash256 NodeId, IPEndPoint Endpoint);

internal readonly record struct ChallengeKey(ValueHash256 NodeId, IPEndPoint Endpoint);

internal readonly record struct PendingNonceKey(IPEndPoint Endpoint, NonceKey Nonce);

internal readonly record struct ResponseKey(ValueHash256 NodeId, RequestId RequestId, MessageType MessageType);

internal readonly record struct SentChallengeExpiry(ChallengeKey Key, long CreatedAtMilliseconds);

internal readonly record struct NonceKey(ulong Prefix, uint Suffix)
{
    public static NonceKey From(ReadOnlySpan<byte> nonce)
        => nonce.Length == PacketCodec.NonceSize
            ? new(
                BinaryPrimitives.ReadUInt64BigEndian(nonce[..sizeof(ulong)]),
                BinaryPrimitives.ReadUInt32BigEndian(nonce.Slice(sizeof(ulong), sizeof(uint))))
            : throw new ArgumentException($"Nonce must be {PacketCodec.NonceSize} bytes.", nameof(nonce));
}

internal sealed record PendingRequest(Node Receiver, Discv5Message Message);

internal readonly record struct SentChallenge(Challenge Challenge, byte[] Packet, long CreatedAtMilliseconds) : IDisposable
{
    public void Dispose() => Challenge.Dispose();
}
