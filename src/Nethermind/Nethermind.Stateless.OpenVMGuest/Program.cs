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
    /// What is proven is the Keccak-256 of the result, not the result itself.
    /// OpenVM's public output is a fixed 32-byte window and the result is 43
    /// bytes (SSZ: a 32-byte root, a flag, a chain id and a schema id), and that
    /// is not a choice this guest can make differently:
    /// <c>DEFAULT_MAX_NUM_PUBLIC_VALUES</c> is 32 and <c>cargo openvm keygen</c>
    /// refuses any other value, so a larger window would mean a proving key off
    /// the supported path. Writing past the window panics rather than truncates.
    /// <para>
    /// THE VERIFIER MUST HASH TOO. Where the ZisK and SP1 guests publish the
    /// result bytes for a verifier to compare directly, a verifier here has to
    /// encode the result it expects and compare Keccak-256 digests. The hash is
    /// one accelerated instruction on OpenVM and costs essentially nothing; the
    /// asymmetry in the contract is what to be careful about.
    /// </para>
    /// </remarks>
    private static partial void WriteOutput(ReadOnlySpan<byte> output)
    {
        // The full result, for debugging: only the digest is provable, so this
        // is the only place the bytes behind it are visible at all. Anything
        // asserting correctness must read the digest, not this line.
        IO.PrintLine(Convert.ToHexStringLower(output));

        Span<byte> digest = stackalloc byte[DigestLength];

        Accelerators.Keccak256(output, digest);

        IO.WriteOutput(digest);
    }
}
