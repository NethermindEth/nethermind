// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    /// <summary>Hashes <paramref name="data"/> into <paramref name="output"/> with SHA-256.</summary>
    /// <remarks>The guest has no BCL implementation to fall back on &mdash; SHA-256 is a zkVM accelerator.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sha256(ReadOnlySpan<byte> data, Span<byte> output) => Accelerators.Sha256(data, output);
}
