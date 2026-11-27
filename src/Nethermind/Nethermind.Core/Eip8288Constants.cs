// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Constants of EIP-8288 (signature aggregation), which replaces the raw witnesses of an EIP-8141 block's
/// <c>LEANSPHINCS_VERIFIER</c> claims in its dependency proof with one recursive STARK.
/// https://eips.ethereum.org/EIPS/eip-8288
/// </summary>
public static class Eip8288Constants
{
    /// <summary>The version byte of an EIP-8141 <c>dependency_proof</c> whose leanSPHINCS witnesses are aggregated.</summary>
    /// <remarks>EIP-8141 reserves later version bytes for aggregation upgrades and suggests <c>0x01</c>; EIP-8288 does
    /// not assign one yet.</remarks>
    public const byte DependencyProofVersionAggregated = 0x01;

    /// <summary>A dependency as the recursive guest takes it: <c>scheme (32B, big-endian) || data_hash (32B) || verification_key (32B)</c>.</summary>
    public const int DependencyTripleLength = 96;

    public const byte LeanSphincsScheme = 0x10;

    /// <summary>Maximum dependencies in one dependency list of the pinned recursive guest, selected, child and discard lists
    /// alike: EIP-8288's output capacity, <c>MAX_RLP_BLOCK_SIZE // 96</c> (87,381).</summary>
    public const int MaxProofDependencies = Eip7934Constants.DefaultMaxRlpBlockSize / DependencyTripleLength;

    /// <summary>EIP-8288 <c>MAX_INNER_PROOFS_PER_STEP</c> (provisional): the most recursive proofs one step of the pinned
    /// recursive guest verifies together.</summary>
    public const int MaxInnerProofsPerStep = 16;

    /// <summary>Maximum recursive proofs one step of the pinned recursive guest folds: the native backend's tree arity.</summary>
    public const int MaxRecursiveInputsPerStep = 16;

    /// <summary>Maximum raw witnesses one native proving call takes; a local bound of the pinned backend.</summary>
    public const int MaxRawWitnessesPerStep = 256;

    /// <summary>The pinned backend's generic leanSTARK program bound. Unused here, but part of the native limits checked at startup.</summary>
    public const int MaxLeanStarkInstructions = 2048;

    /// <summary>Maximum encoded native witness or proof buffer the pinned backend accepts.</summary>
    public const int MaxProofBytes = 8 * 1024 * 1024;

    /// <summary>EIP-8288 <c>MAX_DEPENDENCY_PROOF_BYTES</c> (1 MiB): the bound on the recursive STARK.</summary>
    /// <remarks>Not EIP-8141's <c>MAX_DEPENDENCY_PROOF_BYTES</c>, which bounds the whole dependency proof the STARK sits in.</remarks>
    public const int MaxAggregateProofBytes = 1_048_576;

    /// <summary>Encoded public key (<c>PK.seed || PK.root</c>) and signature of leanVM's leanSPHINCS, as the native backend
    /// takes a witness.</summary>
    public const int LeanSphincsWitnessBytes = 32 + Eip8141Constants.LeanSphincsSignatureSize;

    /// <summary>Maximum native aggregation input: its framing, every inner proof at a full dependency list and largest proof,
    /// the raw witnesses and a full discard list.</summary>
    /// <remarks>The native backend's input ceiling, which startup checks this against.</remarks>
    public const int MaxAggregationInputBytes = 3 * sizeof(uint)
        + MaxInnerProofsPerStep * (2 * sizeof(uint) + MaxProofDependencies * DependencyTripleLength + MaxProofBytes)
        + MaxRawWitnessesPerStep * (DependencyTripleLength + sizeof(uint) + LeanSphincsWitnessBytes)
        + MaxProofDependencies * DependencyTripleLength;

    /// <summary>EIP-8288 <c>AGGREGATED_VK</c>: the Fiat-Shamir key of the recursive guest in the pinned Nethermind.Crypto.Lean
    /// package, which every aggregated dependency proof verifies under.</summary>
    public static ReadOnlySpan<byte> AggregatedVk => _aggregatedVk;

    private static readonly byte[] _aggregatedVk = Convert.FromHexString("645bc491d3d0728b8a0f556be143f845e2ff80aa59da060d750f32cfb05b800c");
}
