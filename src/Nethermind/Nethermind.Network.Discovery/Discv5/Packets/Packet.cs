// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Packets;

internal readonly struct Packet(
    PacketFlag flag,
    ReadOnlyMemory<byte> nonce,
    ReadOnlyMemory<byte> authData,
    ReadOnlyMemory<byte> message,
    byte[] messageAdBuffer,
    int messageAdLength) : IDisposable
{
    private readonly byte[]? _messageAdBuffer = messageAdBuffer;

    public PacketFlag Flag { get; } = flag;

    public ReadOnlyMemory<byte> Nonce { get; } = nonce;

    public ReadOnlyMemory<byte> AuthData { get; } = authData;

    public ReadOnlyMemory<byte> Message { get; } = message;

    public ReadOnlyMemory<byte> MessageAd { get; } = messageAdBuffer.AsMemory(0, messageAdLength);

    public ReadOnlyMemory<byte> ChallengeData => MessageAd;

    public void Dispose()
    {
        if (_messageAdBuffer is null)
        {
            return;
        }

        SafeArrayPool<byte>.Shared.Return(_messageAdBuffer);
    }
}
