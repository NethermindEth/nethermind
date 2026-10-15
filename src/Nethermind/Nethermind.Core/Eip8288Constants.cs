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
    /// <remarks>Below <see cref="MaxDepsPerAggregate"/>: a local proving and verification bound, not a consensus or FOCIL omission
    /// bound. EIP-8288 has no block dependency quota; wrappers and inclusion-list packages within its limits but above this
    /// envelope are refused locally, never treated as invalid.</remarks>
    public const int MaxProofDependencies = 256;

    /// <summary>Minimum encoded size of a frame transaction beyond its dependency triples, in bytes.</summary>
    public const int MinFrameTransactionOverheadBytes = 52;

    /// <summary>Dependencies one <c>MAX_BYTES_PER_INCLUSION_LIST</c> list can hold: 84, as each takes 96 bytes of frame data
    /// after a frame transaction's minimum 52 further bytes.</summary>
    public const int MaxDependenciesPerInclusionList =
        (Eip7805Constants.MaxBytesPerInclusionList - MinFrameTransactionOverheadBytes) / DependencyTripleLength;

    /// <summary>EIP-8288 floor on the aggregate circuit's per-step recursive fan-in for EIP-7805 builder liveness: the
    /// <c>IL_COMMITTEE_SIZE</c> inclusion-list proofs plus the builder's running aggregate of its mempool wrappers.</summary>
    /// <remarks>The pinned prototype guest folds two recursive inputs per step (see <see cref="MaxProofDependencies"/>), so the
    /// managed aggregator reaches this fan-in through a binary tree, costing extra proving rounds on the critical path. A
    /// conforming activation profile must accept this many inputs in one step.</remarks>
    public const int MinRecursiveFanInPerStep = Eip7805Constants.InclusionListCommitteeSize + 1;

    /// <summary>EIP-8288 floor on the aggregate circuit's per-step discard bound: the dependencies of
    /// <c>IL_COMMITTEE_SIZE</c> full inclusion lists, 1,344, since a builder may legitimately exclude any of their
    /// transactions.</summary>
    /// <remarks>The pinned prototype guest discards at most <see cref="MaxProofDependencies"/> per step; the managed aggregator
    /// prunes each child separately, so larger discard sets fold over several steps rather than in one.</remarks>
    public const int MinDiscardsPerStep = Eip7805Constants.InclusionListCommitteeSize * MaxDependenciesPerInclusionList;
    public const int MaxGenericStarkProofs = 16;
    public const int MaxLeanStarkInstructions = 2048;
    public const int MaxInclusionListDependencyBytes = MaxProofDependencies * DependencyTripleLength;

    /// <summary>Maximum encoded native direct witness, such as a mode-0 leanSTARK proof.</summary>
    public const int MaxProofBytes = 8 * 1024 * 1024;

    /// <summary>EIP-8288 <c>MAX_AGGREGATE_PROOF_BYTES</c>: the bound on every serialized recursive aggregate proof, including a
    /// private recursive input, checked before proof decoding or verification.</summary>
    /// <remarks>The spec leaves the value to the activating fork as part of the <see cref="AggregatedVk"/> profile; this is the
    /// pinned prototype profile's value, which the native backend reports back.</remarks>
    public const int MaxAggregateProofBytes = 8 * 1024 * 1024;

    /// <summary>Native acceptance bound for the serialized mixed guest proof, which is the whole <c>stark_proof</c>.</summary>
    public const int MaxMixedGuestProofBytes = MaxAggregateProofBytes;

    /// <summary>Maximum native aggregation input, including two maximum-sized child proofs and metadata.</summary>
    public const int MaxAggregationInputBytes = 18 * 1024 * 1024;

    /// <summary>Maximum encoded ETH header response, including room around a maximum-sized aggregate proof.</summary>
    public const int MaxHeaderResponseBytes = MaxAggregateProofBytes + 1024 * 1024;

    /// <summary>Encoded public key and signature size of the pinned NiceTry/Daisugi Keccak SPHINCS scheme.</summary>
    public const int LeanSphincsWitnessBytes = 32 + 6176;

    public const byte LeanSphincsScheme = 0x10;
    public const byte LeanStarkScheme = 0x11;

    /// <summary>Whether <see cref="LeanStarkScheme"/> is in the EIP-8288 <c>ENABLED_SCHEMES</c>, with <see cref="LeanSphincsScheme"/>.</summary>
    /// <remarks><c>ENABLED_SCHEMES</c> is a per-fork constant that the <see cref="AggregatedVk"/> circuit must accept exactly, so it
    /// changes only with the key. The pinned circuit enables leanSTARK under its generic CPU-proof verification parameters.</remarks>
    public const bool LeanStarkSchemeEnabled = true;

    public const ulong LeanSphincsVerificationGas = 3_000;
    public const ulong LeanStarkVerificationGas = 30_000;

    public const int MaxSigsPerTx = 16;
    public const int MaxStarksPerTx = 1;

    /// <summary>Mempool aggregation cadence in milliseconds.</summary>
    public const int AggregationInterval = 1_000;

    /// <summary>Mode-0 (direct witness) leanSPHINCS dependencies per wrapper (<c>MAX_LEANSIG_DEPS_PER_WRAPPER</c>).</summary>
    public const int MaxLeanSigDepsPerWrapper = 16;

    /// <summary>Mode-0 (direct witness) leanSTARK dependencies per wrapper (<c>MAX_LEANSTARK_DEPS_PER_WRAPPER</c>).</summary>
    public const int MaxLeanStarkDepsPerWrapper = 1;

    /// <summary>Mode-1 (aggregate) dependencies per wrapper (<c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c>), independent of scheme; mode-0
    /// witness limits do not apply.</summary>
    /// <remarks>A provisional mempool bound: it does not limit blocks or inclusion lists and never excuses an omission.</remarks>
    public const int MaxDepsPerAggregate = 4096;

    /// <summary>Transactions per wrapper, in either mode (<c>MAX_TXS_PER_WRAPPER</c>).</summary>
    public const int MaxTxsPerWrapper = 4096;

    /// <summary>Serialized wrapper ceiling, in either mode, in the EIP-8288 wrapper encoding (<c>MAX_WRAPPER_BYTES</c>).</summary>
    /// <remarks>A policy ceiling: clients may refuse smaller wrappers under local limits.</remarks>
    public const int MaxWrapperBytes = 9 * 1024 * 1024;

    /// <summary>Fiat-Shamir key of the recursive guest pinned by tools/lean-ffi.</summary>
    public static ReadOnlySpan<byte> AggregatedVk => _aggregatedVk;

    private static readonly byte[] _aggregatedVk = Convert.FromHexString("00f24bbdf44a31139c975223b77510cd93a905664da3c4a52b5eba4e9b07de9e");
}
