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

    /// <summary>Prototype acceptance bound for the serialized mixed guest proof, which is the whole <c>stark_proof</c>.</summary>
    public const int MaxMixedGuestProofBytes = MaxProofBytes;

    /// <summary>Maximum native aggregation input, including two maximum-sized child proofs and metadata.</summary>
    public const int MaxAggregationInputBytes = 18 * 1024 * 1024;

    /// <summary>Maximum encoded ETH header response, including room around an 8 MiB proof.</summary>
    public const int MaxHeaderResponseBytes = MaxProofBytes + 1024 * 1024;

    /// <summary>Encoded public key and signature size of the pinned NiceTry/Daisugi Keccak SPHINCS scheme.</summary>
    public const int LeanSphincsWitnessBytes = 32 + 6176;

    public const byte LeanSphincsScheme = 0x10;
    public const byte LeanStarkScheme = 0x11;

    /// <summary>Whether <see cref="LeanStarkScheme"/> is in the EIP-8288 <c>enabled_schemes</c>, with <see cref="LeanSphincsScheme"/>.</summary>
    /// <remarks>The set is fixed with <see cref="AggregatedVk"/>, whose circuit enforces it on every dependency list it reads.
    /// The pinned circuit enables leanSTARK under its generic CPU-proof dependency profile; changing this needs a circuit and key to match.</remarks>
    public const bool LeanStarkSchemeEnabled = true;

    public const ulong LeanSphincsVerificationGas = 3_000;
    public const ulong LeanStarkVerificationGas = 30_000;

    public const int MaxSigsPerTx = 16;
    public const int MaxStarksPerTx = 1;

    /// <summary>Mempool aggregation cadence in milliseconds.</summary>
    public const int AggregationInterval = 1_000;

    /// <summary>Mode-0 (direct witness) leanSPHINCS dependencies per wrapper; EIP-8437 <c>MAX_DIRECT_SIGS_PER_WRAPPER</c>.</summary>
    public const int MaxLeanSigDepsPerWrapper = 16;

    /// <summary>Mode-0 (direct witness) leanSTARK dependencies per wrapper; EIP-8437 <c>MAX_DIRECT_STARKS_PER_WRAPPER</c>.</summary>
    public const int MaxLeanStarkDepsPerWrapper = 1;

    /// <summary>Mode-1 (aggregate) dependencies per wrapper; mode-0 witness limits do not apply to an aggregate.</summary>
    public const int MaxDepsPerAggregate = 4096;

    /// <summary>Mode-1 (aggregate) leanSTARK dependencies per wrapper, counted within <see cref="MaxDepsPerAggregate"/>.</summary>
    public const int MaxLeanStarkDepsPerAggregate = 16;

    /// <summary>Transactions per wrapper, in either mode.</summary>
    public const int MaxTxsPerWrapper = 4096;

    /// <summary>Serialized wrapper ceiling, in either mode, using the EIP-8437 kind-1 encoding.</summary>
    /// <remarks>A policy ceiling: clients may refuse smaller wrappers under local limits.</remarks>
    public const int MaxWrapperBytes = 64 * 1024 * 1024;

    /// <summary>Fiat-Shamir key of the recursive guest pinned by tools/lean-ffi.</summary>
    public static ReadOnlySpan<byte> AggregatedVk => _aggregatedVk;

    private static readonly byte[] _aggregatedVk = Convert.FromHexString("c77fc9fe635aa8ba3c34028af0134e155391f8f76ad76f2e598b6823e0e7f7d1");
}
