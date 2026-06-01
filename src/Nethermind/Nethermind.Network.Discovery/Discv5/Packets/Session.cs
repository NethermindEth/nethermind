// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Network.Discovery.Discv5.Packets;

internal sealed record Session(PublicKey RemotePublicKey, byte[] ReadKey, byte[] WriteKey)
{
    private long _nonceCounter;

    public void WriteNextNonce(ICryptoRandom random, Span<byte> nonce)
    {
        if (nonce.Length != PacketCodec.NonceSize)
        {
            throw new ArgumentException($"Nonce must be {PacketCodec.NonceSize} bytes.", nameof(nonce));
        }

        BinaryPrimitives.WriteUInt32BigEndian(nonce, unchecked((uint)Interlocked.Increment(ref _nonceCounter)));
        random.GenerateRandomBytes(nonce[sizeof(uint)..]);
    }
}
