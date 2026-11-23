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
    public const byte DepVerifyFrameMode = 4;

    /// <summary>A dependency triple is <c>scheme (32B, big-endian) || data_hash (32B) || verification_key (32B)</c>.</summary>
    public const int DependencyTripleLength = 96;

    public const int MaxDependenciesPerFrame = 256;

    /// <summary>Maximum dependencies in one dependency list of the pinned recursive guest, selected, child and discard lists alike:
    /// EIP-8288's output capacity, <c>MAX_RLP_BLOCK_SIZE // 96</c> (87,381).</summary>
    /// <remarks>EIP-8288 has no block dependency quota; its draft requires the circuit's output capacity to be at least this, and
    /// a block within EIP-7934's <c>MAX_RLP_BLOCK_SIZE</c> cannot carry more distinct dependency triples. Mode-1 wrappers and
    /// inclusion-list proofs never reach it: their byte limits bound them tighter. Fixed at EIP-7934's default, as the native
    /// backend's bound is, whatever block size limit a chain configures.</remarks>
    public const int MaxProofDependencies = Eip7934Constants.DefaultMaxRlpBlockSize / DependencyTripleLength;

    /// <summary>EIP-8288 <c>MAX_INNER_PROOFS_PER_STEP</c> (provisional): the most raw leanSTARK witnesses and recursive proofs
    /// one step of the pinned recursive guest verifies together, discarded or redundant ones included.</summary>
    /// <remarks>leanSPHINCS witnesses do not count. Lists carry any number of leanSTARK dependencies: more than this many are
    /// folded over several steps.</remarks>
    public const int MaxInnerProofsPerStep = 16;

    /// <summary>Maximum recursive proofs one step of the pinned recursive guest folds: the native backend's tree arity, within
    /// <see cref="MaxInnerProofsPerStep"/>.</summary>
    public const int MaxRecursiveInputsPerStep = 16;

    /// <summary>Maximum raw witnesses of either scheme one native proving call takes.</summary>
    /// <remarks>A local proving-resource bound of the pinned backend, not an EIP-8288 constant: larger sets merge through
    /// recursive inputs.</remarks>
    public const int MaxRawWitnessesPerStep = 256;

    /// <summary>Maximum instructions of a leanSTARK program the pinned generic verifier accepts.</summary>
    public const int MaxLeanStarkInstructions = 2048;

    /// <summary>Maximum encoded native direct witness or proof buffer the pinned backend accepts, such as a mode-0 leanSTARK proof.</summary>
    public const int MaxProofBytes = 8 * 1024 * 1024;

    /// <summary>EIP-8288 <c>MAX_DEPENDENCY_PROOF_BYTES</c> (1 MiB): the bound on a block's serialized dependency proof, and
    /// Nethermind's bound on every other serialized recursive aggregate proof of the pinned <see cref="AggregatedVk"/> guest,
    /// including a private recursive input, in wrappers and inclusion lists, checked before proof hashing, decoding or
    /// verification.</summary>
    /// <remarks>Applying the block bound to every aggregate keeps the prover from producing or accepting a proof no block could
    /// carry. The leanSPHINCS guest's measured 236–278 KB proofs (about 259 KB with mixed dependencies) leave ample margin.</remarks>
    public const int MaxAggregateProofBytes = 1_048_576;

    /// <summary>EIP-8288 <c>MAX_IL_PROOF_BYTES</c> (1.25 MiB): the bound on an inclusion list's serialized SSZ
    /// <c>InclusionListProof</c> container, both fields and their offsets included, plus the four-byte offset the container
    /// adds to the signed <c>InclusionList</c>.</summary>
    /// <remarks>Provisional.</remarks>
    public const int MaxIlProofBytes = 1_310_720;

    /// <summary>Fixed bytes counted against <see cref="MaxIlProofBytes"/> besides both fields: the container's two field offsets
    /// and its own offset in the signed <c>InclusionList</c>.</summary>
    public const int IlProofOverheadBytes = 3 * sizeof(uint);

    /// <summary>The bytes an <c>InclusionListProof</c> counts against <see cref="MaxIlProofBytes"/>.</summary>
    public static long IlProofBytes(int starkProofLength, int proofDepCount) =>
        IlProofOverheadBytes + (long)starkProofLength + (long)proofDepCount * DependencyTripleLength;

    /// <summary>Maximum native aggregation input: its framing, every inner proof at a recursive input's full dependency list and
    /// largest proof, the raw signatures and a full discard list.</summary>
    /// <remarks>The native backend's input ceiling, which startup checks this against, not a practical bound: real steps are far
    /// smaller, since aggregate proofs are capped at <see cref="MaxAggregateProofBytes"/>.</remarks>
    public const int MaxAggregationInputBytes = 3 * sizeof(uint)
        + MaxInnerProofsPerStep * (2 * sizeof(uint) + MaxProofDependencies * DependencyTripleLength + MaxProofBytes)
        + MaxRawWitnessesPerStep * (DependencyTripleLength + sizeof(uint) + LeanSphincsWitnessBytes)
        + MaxProofDependencies * DependencyTripleLength;

    /// <summary>Encoded public key (<c>PK.seed ‖ PK.root</c>) and signature size of leanVM's leanSPHINCS.</summary>
    public const int LeanSphincsWitnessBytes = 32 + 7740;

    public const byte LeanSphincsScheme = 0x10;
    /// <summary>EIP-8288 <c>LEANSTARK_SCHEME</c>.</summary>
    /// <remarks>Verified under the pinned guest's generic leanVM proof profile: the key is <c>get_vk_hash</c> of the program's
    /// committed bytecode key and the proof shows <c>get_claim_hash(data_hash)</c>.</remarks>
    public const byte LeanStarkScheme = 0x11;

    public const ulong LeanSphincsVerificationGas = 3_000;
    public const ulong LeanStarkVerificationGas = 30_000;

    public const int MaxSigsPerTx = 16;
    public const int MaxStarksPerTx = 1;

    /// <summary>Mempool aggregation cadence in milliseconds.</summary>
    public const int AggregationInterval = 1_000;

    /// <summary>leanSPHINCS dependencies per mode-0 wrapper (<c>MAX_LEANSIG_DEPS_PER_WRAPPER</c>). Mode 1 has no wrapper-level
    /// dependency-count limit; <see cref="MaxWrapperBytes"/> bounds it.</summary>
    public const int MaxLeanSigDepsPerWrapper = 16;

    /// <summary>leanSTARK dependencies per mode-0 wrapper (<c>MAX_LEANSTARK_DEPS_PER_WRAPPER</c>).</summary>
    public const int MaxLeanStarkDepsPerWrapper = 1;

    /// <summary>The most dependency triples a wrapper within <see cref="MaxWrapperBytes"/> can encode, each at least a 98-byte RLP
    /// string: an allocation bound for decoding either mode, not a dependency rule.</summary>
    public const int MaxWrapperDependencies = MaxWrapperBytes / (2 + DependencyTripleLength);

    /// <summary>EIP-8288 <c>MAX_WRAPPER_BYTES</c>: the complete canonical wrapper encoding, full transactions included, in either
    /// mode. Equal to the <c>eth</c> soft response limit, so EIP-8437 retrieves each wrapper whole.</summary>
    public const int MaxWrapperBytes = 2 * 1024 * 1024;

    /// <summary>Fiat-Shamir key of the recursive guest in the pinned Nethermind.Crypto.Lean package.</summary>
    public static ReadOnlySpan<byte> AggregatedVk => _aggregatedVk;

    private static readonly byte[] _aggregatedVk = Convert.FromHexString("645bc491d3d0728b8a0f556be143f845e2ff80aa59da060d750f32cfb05b800c");
}
