// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Constants of EIP-8288 (frame type for PQ signature and STARK aggregation), which extends the
/// EIP-8141 frame transaction with dependency-verification frames and a block-level recursive STARK.
/// https://eips.ethereum.org/EIPS/eip-8288
/// </summary>
public static class Eip8288Constants
{
    /// <summary>Frame mode of a dependency-verification frame (in addition to the EIP-8141 modes).</summary>
    // EIP-8288 drafts use 3, already allocated to EIP-7906 POST_TX in this prototype.
    public const byte DepVerifyFrameMode = 4;

    /// <summary>A dependency triple is <c>scheme (32B, big-endian) || data_hash (32B) || verification_key (32B)</c>.</summary>
    public const int DependencyTripleLength = 96;

    public const int MaxDependenciesPerFrame = 256;

    /// <summary>Maximum distinct dependencies covered by the prototype native proof envelope.</summary>
    public const int MaxProofDependencies = 256;
    public const int MaxGenericStarkProofs = 16;
    public const int MaxLeanStarkInstructions = 2048;
    public const int MaxInclusionListDependencyBytes = MaxProofDependencies * DependencyTripleLength;

    /// <summary>Maximum encoded native witness or recursive proof size.</summary>
    public const int MaxProofBytes = 8 * 1024 * 1024;

    /// <summary>Prototype acceptance bound for the single serialized mixed guest proof, reserved in aggregate capacity.</summary>
    public const int MaxMixedGuestProofBytes = MaxProofBytes - 12 - MaxProofDependencies * DependencyTripleLength;

    /// <summary>Maximum native aggregation input, including two maximum-sized child proofs and metadata.</summary>
    public const int MaxAggregationInputBytes = 18 * 1024 * 1024;

    /// <summary>Maximum encoded ETH header response, including room around an 8 MiB proof.</summary>
    public const int MaxHeaderResponseBytes = MaxProofBytes + 1024 * 1024;

    /// <summary>Encoded public key and signature size of the pinned NiceTry/Daisugi Keccak SPHINCS scheme.</summary>
    public const int LeanSphincsWitnessBytes = 32 + 6176;

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

    /// <summary>Fiat-Shamir key of the recursive guest pinned by tools/lean-ffi.</summary>
    public static ReadOnlySpan<byte> AggregatedVk => _aggregatedVk;

    private static readonly byte[] _aggregatedVk = Convert.FromHexString("9370d760abb55fdf02acc7e8d40688c425815c3d25a2aea3c030b2ae1ab51ace");
}
