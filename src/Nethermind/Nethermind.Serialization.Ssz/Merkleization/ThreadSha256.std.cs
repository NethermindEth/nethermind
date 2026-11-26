// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;

namespace Nethermind.Serialization.Ssz.Merkleization;

/// <summary>Computes SHA-256 using Arm instructions for 64-byte messages and a per-thread SHA-256 instance otherwise.</summary>
/// <remarks>
/// On OpenSSL, <see cref="SHA256.HashData(ReadOnlySpan{byte}, Span{byte})"/> sets up a digest context per hash, which
/// contends across threads, while a kept instance only resets its own.
/// </remarks>
public static class ThreadSha256
{
    [ThreadStatic] private static SHA256? s_sha256;

    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short for the hash.</exception>
    public static void HashData(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (Sha256.IsSupported && source.Length == 64 && destination.Length >= SHA256.HashSizeInBytes)
        {
            ArmSha256.HashOne(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(destination));
            return;
        }

        if (!(s_sha256 ??= SHA256.Create()).TryComputeHash(source, destination, out _))
            throw new ArgumentException("Destination is too short.", nameof(destination));
    }

    /// <inheritdoc cref="HashData(ReadOnlySpan{byte}, Span{byte})"/>
    public static byte[] HashData(ReadOnlySpan<byte> source)
    {
        byte[] hash = new byte[SHA256.HashSizeInBytes];
        HashData(source, hash);
        return hash;
    }
}
