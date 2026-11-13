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
    /// <remarks>The dependency-list limit of the circuit behind the pinned <see cref="AggregatedVk"/>, not a FOCIL omission bound;
    /// EIP-8288 has no block dependency quota. Mode-1 wrapper proofs above it are invalid (EIP-8288), discarded without penalty;
    /// an inclusion-list proof above it imposes no obligation; a block whose dependencies exceed it is invalid here, since no
    /// proof under that key can cover it, as with <see cref="MaxAggregateProofBytes"/>. EIP-8288 requires an aggregation output
    /// capacity of at least <c>MAX_RLP_BLOCK_SIZE // 96</c>, which this prototype guest does not reach.</remarks>
    public const int MaxProofDependencies = 256;

    /// <summary>Batch-pricing EIP (ethereum/EIPs#12664) <c>MAX_CLAIMS_PER_BATCH_LEAF</c> (provisional): claims one leanSTARK leaf
    /// may prove.</summary>
    /// <remarks>A parameter of the guest behind <see cref="BatchedAggregatedVk"/>, so changing it changes that key; EIP-8288's guest
    /// takes one-claim leaves only. It bounds a single leaf only; recursive aggregation combines leaves into larger dependency
    /// lists.</remarks>
    public const int MaxClaimsPerBatchLeaf = 256;

    /// <summary>EIP-8288 <c>MAX_INNER_PROOFS_PER_STEP</c> (provisional, 16 in the EIP): direct leanSTARK proofs, batch leaves and
    /// recursive proofs verified by one aggregation step together.</summary>
    /// <remarks>Raw leanSPHINCS witnesses do not count. Discarded and redundant inputs do; the pinned guest verifies up to this
    /// many in one execution. Chosen with the leaf bounds, as EIPs#12654 requires: in the pinned guest 13 maximum-resource leaves
    /// fit a step's 2^30 committed-witness bound and 14 do not; 12 leaves room for the leanSPHINCS signatures a step may add.</remarks>
    public const int MaxInnerProofsPerStep = 12;

    /// <summary>Maximum instructions of a leanSTARK program the pinned generic verifier accepts.</summary>
    /// <remarks>An EIP-8288 verifier resource bound counting the compiled program padded to a power of two, support code
    /// included. Like every leanSTARK resource bound, it applies to each leaf at direct admission and in aggregation, even
    /// when all its claims are discarded.</remarks>
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

    /// <summary>Maximum native aggregation input, including two maximum-sized child proofs and metadata.</summary>
    public const int MaxAggregationInputBytes = 18 * 1024 * 1024;

    /// <summary>Encoded public key and signature size of the pinned NiceTry/Daisugi Keccak SPHINCS scheme.</summary>
    public const int LeanSphincsWitnessBytes = 32 + 6176;

    public const byte LeanSphincsScheme = 0x10;
    /// <summary>EIP-8288 <c>LEANSTARK_SCHEME</c>, enabled here as a deviation from the EIP.</summary>
    /// <remarks>The EIP reserves it until <c>get_deps_hash</c> is defined for it. This node enables it with the pinned guest's
    /// generic leanVM proof profile and the split-domain digest of <see cref="Eip8288Dependencies.ComputeDepsHash"/>, which leaves
    /// every leanSPHINCS-only list at the spec digest.</remarks>
    public const byte LeanStarkScheme = 0x11;

    public const ulong LeanSphincsVerificationGas = 3_000;
    public const ulong LeanStarkVerificationGas = 30_000;

    /// <summary>Batch-pricing EIP (ethereum/EIPs#12664) <c>BATCHED_LEANSTARK_SCHEME</c> (provisional): a claim of a sponsored
    /// leaf, <c>data_hash</c> the claim and <c>verification_key_hash</c> the leaf's key.</summary>
    public const byte BatchedLeanStarkScheme = 0x12;

    /// <summary>Batch-pricing EIP <c>LEANSTARK_LEAF_PAYMENT_SCHEME</c> (provisional): a leaf's payment, <c>data_hash</c> its nonzero
    /// <c>get_batch_hash(claims)</c> and <c>verification_key_hash</c> its key. Effective only in the block of the claims it
    /// sources.</summary>
    public const byte LeanStarkLeafPaymentScheme = 0x13;

    /// <summary>Batch-pricing EIP <c>LEANSPHINCS_VERIFICATION_GAS</c> (provisional): from its activation, a leanSPHINCS
    /// declaration's frame charge in place of <see cref="LeanSphincsVerificationGas"/>.</summary>
    public const ulong BatchPricedLeanSphincsVerificationGas = 15_400;

    /// <summary>Batch-pricing EIP <c>LEANSTARK_VERIFICATION_GAS</c> (provisional): from its activation, a leanSTARK declaration's
    /// frame charge, its recursive child slot included, in place of <see cref="LeanStarkVerificationGas"/>.</summary>
    public const ulong BatchPricedLeanStarkVerificationGas = 572_000;

    /// <summary>Batch-pricing EIP <c>RECURSIVE_PROOF_SHARE_GAS</c> (provisional): from its activation, each ordinary declared
    /// dependency's intrinsic share of the block's recursive proof, in place of EIP-8288's <see cref="LeanStarkVerificationGas"/>
    /// (<see cref="Eip8288Dependencies.RecursiveStarkGas"/>).</summary>
    public const ulong RecursiveProofShareGas = 4_200;

    /// <summary>Batch-pricing EIP <c>LEANSTARK_CLAIM_GAS</c> (provisional): a batch claim's frame charge, in place of EIP-8288's
    /// proof share.</summary>
    public const ulong LeanStarkClaimGas = 4_200;

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

    /// <summary>Fiat-Shamir key of EIP-8288's recursive guest in the pinned Nethermind.Crypto.Lean package.</summary>
    public static ReadOnlySpan<byte> AggregatedVk => _aggregatedVk;

    /// <summary>Fiat-Shamir key of the package's recursive guest with batch pricing (ethereum/EIPs#12664), which forks enabling
    /// <see cref="Specs.IReleaseSpec.IsEipBatchedDependencyPricingEnabled"/> verify with.</summary>
    /// <remarks>Built from the same source as <see cref="AggregatedVk"/>'s guest, it also takes leaves of up to
    /// <see cref="MaxClaimsPerBatchLeaf"/> claims, establishing <see cref="BatchedLeanStarkScheme"/> claims bound to their
    /// <see cref="LeanStarkLeafPaymentScheme"/> payment, and finalizes them.</remarks>
    public static ReadOnlySpan<byte> BatchedAggregatedVk => _batchedAggregatedVk;

    private static readonly byte[] _aggregatedVk = Convert.FromHexString("86110d71079c44fbae0561223e7201108097b80f34aea13a23e9b96be9b7adaa");
    private static readonly byte[] _batchedAggregatedVk = Convert.FromHexString("df3a802307fe9831311df1288864e5ab0e672b19a224f6b23a374fc873a69828");
}
