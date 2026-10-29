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

    /// <summary>Maximum distinct dependencies covered by one proof of the pinned native backend.</summary>
    /// <remarks>A local proving and verification bound, not a FOCIL omission bound; EIP-8288 has no block dependency quota. Wrappers
    /// and inclusion-list proofs above it are refused locally, without penalty, but a block whose dependencies exceed it is invalid
    /// here: no proof under the pinned <see cref="AggregatedVk"/> can cover it, as with <see cref="MaxAggregateProofBytes"/>.</remarks>
    public const int MaxProofDependencies = 256;

    /// <summary>Maximum leanSTARK dependencies in one dependency list of the pinned recursive guest, selected, child and discard
    /// lists alike.</summary>
    /// <remarks>A local proving and verification bound like <see cref="MaxProofDependencies"/>: a statement over more is split or
    /// deferred by the builder, and a wrapper or inclusion-list proof over it is refused locally, without penalty, but a block over
    /// it is invalid here, since no proof under the pinned <see cref="AggregatedVk"/> can cover it.</remarks>
    public const int MaxGenericStarkProofs = 16;

    /// <summary>Maximum instructions of a leanSTARK program the pinned generic verifier accepts.</summary>
    public const int MaxLeanStarkInstructions = 2048;

    /// <summary>Maximum encoded native direct witness or proof buffer the pinned backend accepts, such as a mode-0 leanSTARK proof.</summary>
    public const int MaxProofBytes = 8 * 1024 * 1024;

    /// <summary>Local bound (1.25 MiB) on every serialized recursive aggregate proof of the pinned <see cref="AggregatedVk"/>
    /// guest, including a private recursive input, in blocks, wrappers and inclusion lists alike, checked before proof decoding
    /// or verification.</summary>
    /// <remarks>Not an EIP-8288 constant: the EIP no longer bounds <c>stark_proof</c> and leaves its size to the proof system.
    /// This is a pinned-prover limit with margin above the guest's measured 265–326 KB root proofs; a guest or key change
    /// must revisit it.</remarks>
    public const int MaxAggregateProofBytes = 1_310_720;

    /// <summary>EIP-8288 <c>MAX_IL_PROOF_BYTES</c> (1.25 MiB): the bound on an inclusion list's serialized SSZ
    /// <c>InclusionListProof</c> container, both fields and their offsets included, plus the four-byte offset the container
    /// adds to the signed <c>InclusionList</c>.</summary>
    /// <remarks>Provisional, like <see cref="MaxAggregateProofBytes"/>.</remarks>
    public const int MaxIlProofBytes = 1_310_720;

    /// <summary>Fixed bytes counted against <see cref="MaxIlProofBytes"/> besides both fields: the container's two field offsets
    /// and its own offset in the signed <c>InclusionList</c>.</summary>
    public const int IlProofOverheadBytes = 3 * sizeof(uint);

    /// <summary>The bytes an <c>InclusionListProof</c> counts against <see cref="MaxIlProofBytes"/>.</summary>
    public static long IlProofBytes(int starkProofLength, int proofDepCount) =>
        IlProofOverheadBytes + (long)starkProofLength + (long)proofDepCount * DependencyTripleLength;

    /// <summary>Native acceptance bound for the serialized mixed guest proof, which the pinned backend reports back.</summary>
    /// <remarks>Above <see cref="MaxAggregateProofBytes"/>, which every caller checks first.</remarks>
    public const int MaxMixedGuestProofBytes = 8 * 1024 * 1024;

    /// <summary>Maximum native aggregation input, including two maximum-sized child proofs and metadata.</summary>
    public const int MaxAggregationInputBytes = 18 * 1024 * 1024;

    /// <summary>Encoded public key and signature size of the pinned NiceTry/Daisugi Keccak SPHINCS scheme.</summary>
    public const int LeanSphincsWitnessBytes = 32 + 6176;

    public const byte LeanSphincsScheme = 0x10;
    /// <summary>EIP-8288 <c>LEANSTARK_SCHEME</c>, enabled here as a deviation from the EIP.</summary>
    /// <remarks>The EIP reserves it until <c>get_deps_hash</c> is defined for it. This node enables it with the pinned guest's
    /// generic CPU-proof profile and the split-domain digest of <see cref="Eip8288Dependencies.ComputeDepsHash"/>, which leaves
    /// every leanSPHINCS-only list at the spec digest.</remarks>
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

    /// <summary>Fiat-Shamir key of the recursive guest pinned by tools/lean-ffi.</summary>
    public static ReadOnlySpan<byte> AggregatedVk => _aggregatedVk;

    private static readonly byte[] _aggregatedVk = Convert.FromHexString("417152e8bd83e0dfb6f3e2bb8991e948f62c7f15d74b693b55979a40b239efa1");
}
