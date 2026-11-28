// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// The EIP-8288 form of the EIP-8141 block <c>dependency_proof</c>: one recursive STARK in place of the raw witnesses of
/// the block's <c>LEANSPHINCS_VERIFIER</c> dependencies, beside the raw witnesses of the rest.
/// </summary>
/// <remarks>
/// Encoded as <c>DEPENDENCY_PROOF_VERSION_AGGREGATED || rlp([stark_proof, [witness, ...]])</c>, the witnesses those of the
/// dependencies that are not leanSPHINCS ones, in <c>block_dependencies</c> order. The STARK proves
/// <c>get_deps_hash</c> of the leanSPHINCS dependencies, as <c>(data_hash, verification_key_hash) = (resolved_msg,
/// resolved_key)</c>, under the protocol's <c>AGGREGATED_VK</c>. A block without leanSPHINCS dependencies uses the raw form.
/// </remarks>
public static class AggregatedDependencyProof
{
    public const string Malformed = "aggregated dependency proof is malformed";
    public const string NothingAggregated = "aggregated dependency proof aggregates no leanSPHINCS dependency";
    public const string StarkTooLarge = "aggregated dependency proof STARK exceeds EIP-8288 MAX_DEPENDENCY_PROOF_BYTES";
    public const string InvalidStark = "aggregated dependency proof STARK does not verify";

    private static readonly RlpLimit WitnessesCountLimit = RlpLimit.For<Block>(Eip8141Constants.MaxDependencyProofBytes, nameof(Block.DependencyProof));

    /// <summary>Whether <paramref name="dependency"/> is one EIP-8288 aggregates.</summary>
    public static bool IsAggregated(in FrameTxDependencyProof.Dependency dependency) => Eip8288Dependencies.IsAggregatable(dependency.Verifier);

    /// <summary>The leanSPHINCS dependencies among <paramref name="dependencies"/>, in canonical EIP-8288 order.</summary>
    public static List<AggregatedDependency> Aggregated(IReadOnlyList<FrameTxDependencyProof.Dependency> dependencies)
    {
        List<AggregatedDependency> aggregated = [];
        foreach (FrameTxDependencyProof.Dependency dependency in dependencies)
        {
            if (IsAggregated(dependency)) aggregated.Add(Eip8288Dependencies.Map(dependency.Key, dependency.Msg));
        }

        return Eip8288Dependencies.Canonicalize(aggregated);
    }

    /// <summary>Encodes <paramref name="starkProof"/> and the raw <paramref name="witnesses"/> it sits beside.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> starkProof, IReadOnlyList<byte[]> witnesses)
    {
        int witnessesLength = 0;
        foreach (byte[] witness in witnesses) witnessesLength += Rlp.LengthOf(witness);
        int contentLength = Rlp.LengthOf(starkProof) + Rlp.LengthOfSequence(witnessesLength);

        byte[] proof = new byte[1 + Rlp.LengthOfSequence(contentLength)];
        proof[0] = Eip8288Constants.DependencyProofVersionAggregated;
        RlpWriter writer = RlpWriter.ContentWriter(proof.AsSpan(1));
        RlpSequence outer = writer.StartSequence(contentLength);
        writer.Encode(starkProof);
        RlpSequence inner = writer.StartSequence(witnessesLength);
        foreach (byte[] witness in witnesses) writer.Encode(witness);
        writer.EndSequence(inner);
        writer.EndSequence(outer);
        return proof;
    }

    /// <summary>Decodes an aggregated proof into its STARK and raw witnesses.</summary>
    public static bool TryDecode(byte[] proof, out byte[] starkProof, out byte[][] witnesses)
    {
        starkProof = [];
        witnesses = [];
        if (proof.Length < 2 || proof[0] != Eip8288Constants.DependencyProofVersionAggregated) return false;

        try
        {
            RlpReader reader = new(proof.AsSpan(1));
            RlpReader content = reader.EnterSequence(out int contentLength);
            starkProof = content.ReadBytes().ToArray();
            witnesses = content.DecodeByteArrays(WitnessesCountLimit);
            if (content.HasNext) return false;
            reader.ExitSequence(content, contentLength);
            return !reader.HasNext;
        }
        catch (RlpReaderException)
        {
            return false;
        }
    }

    /// <summary>Checks an aggregated proof against <paramref name="dependencies"/>, the block's sorted dependencies, and
    /// returns their witnesses, <see cref="FrameTxClaimVerifier.AggregatedWitness"/> for each the STARK proves.</summary>
    /// <exception cref="InvalidBlockException">The proof is malformed, does not fit the dependencies, or its STARK does not verify.</exception>
    /// <exception cref="InvalidOperationException">The verifier failed rather than reject the STARK, so no verdict was reached.</exception>
    public static byte[][] Verify(Block block, FrameTxDependencyProof.Dependency[] dependencies, byte[] proof, ILeanProofVerifier verifier)
    {
        if (!TryDecode(proof, out byte[] starkProof, out byte[][] raw)) throw new InvalidBlockException(block, Malformed);
        if (starkProof.Length > Eip8288Constants.MaxAggregateProofBytes) throw new InvalidBlockException(block, StarkTooLarge);

        byte[][] witnesses = new byte[dependencies.Length][];
        int rawIndex = 0;
        for (int j = 0; j < dependencies.Length; j++)
        {
            if (IsAggregated(dependencies[j]))
            {
                witnesses[j] = FrameTxClaimVerifier.AggregatedWitness;
            }
            else
            {
                if (rawIndex == raw.Length) throw new InvalidBlockException(block, FrameTxDependencyProof.WitnessCountMismatch);
                witnesses[j] = raw[rawIndex++];
            }
        }

        if (rawIndex != raw.Length) throw new InvalidBlockException(block, FrameTxDependencyProof.WitnessCountMismatch);

        List<AggregatedDependency> aggregated = Aggregated(dependencies);
        if (aggregated.Count == 0) throw new InvalidBlockException(block, NothingAggregated);

        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(aggregated);
        if (starkProof.Length == 0 || !verifier.VerifyDependencyProof(in depsHash, Eip8288Constants.AggregatedVk, starkProof))
            throw new InvalidBlockException(block, InvalidStark);

        return witnesses;
    }
}
