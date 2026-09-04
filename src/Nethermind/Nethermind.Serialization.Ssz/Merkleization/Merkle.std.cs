// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    /// <summary>Hashes <paramref name="data"/> into <paramref name="output"/> with SHA-256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sha256(ReadOnlySpan<byte> data, Span<byte> output) => SHA256.HashData(data, output);
}
