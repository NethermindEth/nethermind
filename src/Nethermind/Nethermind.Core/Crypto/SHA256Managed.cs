// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if ZKVM
using System;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Core.Crypto;

/// <summary>
/// Managed SHA-256 wrapper using BouncyCastle for zkVM-compatibility.
/// </summary>
public static class SHA256Managed
{
    public static byte[] HashData(ReadOnlySpan<byte> source)
    {
        Sha256Digest digest = new();
        digest.BlockUpdate(source);
        byte[] result = new byte[digest.GetDigestSize()];
        digest.DoFinal(result);
        return result;
    }
}
#endif
