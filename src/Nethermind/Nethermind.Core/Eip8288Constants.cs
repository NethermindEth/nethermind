// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Constants of EIP-8288 (frame type for PQ signature and STARK aggregation), which extends the
/// EIP-8141 frame transaction with dependency-verification frames and a block-level recursive STARK.
/// https://eips.ethereum.org/EIPS/eip-8288
/// </summary>
public static class Eip8288Constants
{
    /// <summary>Frame mode of a dependency-verification frame (in addition to the EIP-8141 modes).</summary>
    public const byte DepVerifyFrameMode = 3;

    /// <summary>A dependency triple is <c>scheme (32B, big-endian) || data_hash (32B) || verification_key (32B)</c>.</summary>
    public const int DependencyTripleLength = 96;

    public const int MaxDependenciesPerFrame = 256;

    public const byte LeanSphincsScheme = 0x10;
    public const byte LeanStarkScheme = 0x11;

    public const ulong LeanSphincsVerificationGas = 3_000;
    public const ulong LeanStarkVerificationGas = 30_000;

    public const int MaxSigsPerTx = 16;
    public const int MaxStarksPerTx = 1;

    /// <summary>Mempool aggregation cadence in milliseconds.</summary>
    public const int AggregationInterval = 1_000;

    public const int MaxLeanSigDepsPerWrapper = 16;
    public const int MaxLeanStarkDepsPerWrapper = 1;

    /// <summary>
    /// Protocol-level verification key for the block-level recursive STARK. The spec marks it
    /// <c>TBD</c> (derived from the finalized Lean Ethereum STARK circuit); a placeholder until then.
    /// </summary>
    public static readonly byte[] AggregatedVk = new byte[32];
}
