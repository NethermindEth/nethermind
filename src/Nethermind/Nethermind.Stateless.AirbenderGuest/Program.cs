// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Stateless.Guest;

partial class Program
{
    /// <summary>Length in bytes of the Keccak-256 digest this guest publishes.</summary>
    const int DigestLength = 32;

    /// <inheritdoc/>
    /// <remarks>
    /// What is proven is the Keccak-256 of the result, not the result itself: Airbender's public
    /// output is the eight words in x10-x17 when the program finishes, and the result is 43 bytes.
    /// A verifier encodes the result it expects and compares digests, as for OpenVM.
    /// </remarks>
    private static partial void WriteOutput(ReadOnlySpan<byte> output)
    {
        // The full result, for debugging only: anything asserting correctness must read the digest.
        IO.PrintLine(Convert.ToHexStringLower(output));

        Span<byte> digest = stackalloc byte[DigestLength];

        Accelerators.Keccak256(output, digest);

        IO.WriteOutput(digest);
    }
}
