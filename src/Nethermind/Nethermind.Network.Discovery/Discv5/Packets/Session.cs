// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Network.Discovery.Discv5.Packets;

internal sealed record Session(PublicKey RemotePublicKey, byte[] ReadKey, byte[] WriteKey) : IDisposable
{
    public const int KeySize = 16;

    private readonly object _lock = new();
    private long _nonceCounter;
    private bool _disposed;

    public void WriteNextNonce(ICryptoRandom random, Span<byte> nonce)
    {
        if (nonce.Length != PacketCodec.NonceSize)
        {
            throw new ArgumentException($"Nonce must be {PacketCodec.NonceSize} bytes.", nameof(nonce));
        }

        BinaryPrimitives.WriteUInt32BigEndian(nonce, unchecked((uint)Interlocked.Increment(ref _nonceCounter)));
        random.GenerateRandomBytes(nonce[sizeof(uint)..]);
    }

    public bool TryCopyReadKey(Span<byte> destination) => TryCopyKey(ReadKey, destination);

    public bool TryCopyWriteKey(Span<byte> destination) => TryCopyKey(WriteKey, destination);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CryptographicOperations.ZeroMemory(ReadKey);
            CryptographicOperations.ZeroMemory(WriteKey);
        }
    }

    private bool TryCopyKey(byte[] key, Span<byte> destination)
    {
        if (destination.Length != KeySize)
        {
            throw new ArgumentException($"Key destination must be {KeySize} bytes.", nameof(destination));
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            key.CopyTo(destination);
            return true;
        }
    }
}
