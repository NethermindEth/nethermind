// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// The EIP-8141 block <c>dependency_proof</c>: one raw witness per distinct claim dependency of the block's frame
/// transactions, committed to by <see cref="BlockHeader.DependencyProofHash"/> and carried beside the block.
/// </summary>
/// <remarks>Encoded as the empty string for a block without dependencies, and otherwise as
/// <c>DEPENDENCY_PROOF_VERSION_RAW || rlp([witness_0, ...])</c>, the witnesses in <see cref="BlockDependencies"/> order.</remarks>
public static class FrameTxDependencyProof
{
    public const string Unavailable = "dependency proof is not available";
    public const string TooLarge = "dependency proof exceeds MAX_DEPENDENCY_PROOF_BYTES";
    public const string HashMismatch = "dependency proof does not match the header's dependency_proof_hash";
    public const string Malformed = "dependency proof is malformed";
    public const string WitnessCountMismatch = "dependency proof must carry exactly one witness per block dependency";
    public const string WitnessLengthMismatch = "dependency proof witness has the wrong length";
    public const string MissingWitness = "frame transaction carries no witness for its claim";

    private static readonly RlpLimit WitnessesCountLimit = RlpLimit.For<Block>(Eip8141Constants.MaxDependencyProofBytes, nameof(Block.DependencyProof));

    /// <summary>EIP-8141 <c>resolve_dependency</c>: the <c>(verifier, resolved_key, resolved_msg, gas, witness_len)</c> a claim declares.</summary>
    /// <remarks>Ordered as the spec's tuples are: the byte strings lexicographically, then the integers.</remarks>
    public readonly record struct Dependency(Address Verifier, ValueHash256 Key, ValueHash256 Msg, ulong GasLimit, ulong WitnessLength) : IComparable<Dependency>
    {
        public int CompareTo(Dependency other)
        {
            int result = Verifier.CompareTo(other.Verifier);
            if (result == 0) result = Key.CompareTo(other.Key);
            if (result == 0) result = Msg.CompareTo(other.Msg);
            if (result == 0) result = GasLimit.CompareTo(other.GasLimit);
            return result == 0 ? WitnessLength.CompareTo(other.WitnessLength) : result;
        }
    }

    /// <summary>The dependencies of <paramref name="tx"/>'s claims, index for index.</summary>
    public static Dependency[] Resolve(Transaction tx)
    {
        TxFrameClaim[] claims = tx.FrameClaims ?? [];
        Dependency[] dependencies = new Dependency[claims.Length];
        ValueHash256? sigHash = null;
        for (int i = 0; i < claims.Length; i++)
        {
            TxFrameClaim claim = claims[i];
            ValueHash256 msg = claim.ResolveMsg(claim.SignsCanonicalHash ? sigHash ??= FrameTxSigHash.ComputeValue(tx) : default);
            dependencies[i] = new Dependency(claim.Verifier, claim.ResolveKey(tx.SenderAddress!), msg, claim.ResolvedGas, claim.ResolvedWitnessLength);
        }

        return dependencies;
    }

    /// <summary>EIP-8141 <c>block_dependencies</c>: the block's claim dependencies, sorted and deduplicated.</summary>
    public static Dependency[] BlockDependencies(Block block) => BlockDependencies(block, out _);

    private static Dependency[] BlockDependencies(Block block, out Dependency[]?[] perTransaction)
    {
        Transaction[] transactions = block.Transactions;
        perTransaction = new Dependency[]?[transactions.Length];
        SortedSet<Dependency> dependencies = [];
        for (int i = 0; i < transactions.Length; i++)
        {
            if (transactions[i].Type != TxType.FrameTx) continue;

            Dependency[] resolved = Resolve(transactions[i]);
            perTransaction[i] = resolved;
            dependencies.UnionWith(resolved);
        }

        Dependency[] sorted = new Dependency[dependencies.Count];
        dependencies.CopyTo(sorted);
        return sorted;
    }

    /// <summary>Encodes <paramref name="witnesses"/> as a dependency proof; none encodes as the empty string.</summary>
    public static byte[] Encode(IReadOnlyList<byte[]> witnesses)
    {
        if (witnesses.Count == 0) return [];

        int contentLength = 0;
        foreach (byte[] witness in witnesses) contentLength += Rlp.LengthOf(witness);

        byte[] proof = new byte[1 + Rlp.LengthOfSequence(contentLength)];
        proof[0] = Eip8141Constants.DependencyProofVersionRaw;
        RlpWriter writer = RlpWriter.ContentWriter(proof.AsSpan(1));
        RlpSequence sequence = writer.StartSequence(contentLength);
        foreach (byte[] witness in witnesses) writer.Encode(witness);
        writer.EndSequence(sequence);
        return proof;
    }

    /// <summary>Decodes a non-empty dependency proof into its witnesses.</summary>
    public static bool TryDecode(byte[] proof, out byte[][] witnesses)
    {
        witnesses = [];
        if (proof.Length < 2 || proof[0] != Eip8141Constants.DependencyProofVersionRaw) return false;

        try
        {
            RlpReader reader = new(proof.AsSpan(1));
            witnesses = reader.DecodeByteArrays(WitnessesCountLimit);
            return !reader.HasNext;
        }
        catch (RlpReaderException)
        {
            return false;
        }
    }

    /// <summary>Checks <paramref name="block"/>'s dependency proof against its header and dependencies, and assigns each
    /// frame transaction the witnesses of its claims, which its processing then verifies.</summary>
    /// <remarks>A custom verifier runs in the pure context, which reads nothing a transaction before it can change but
    /// the verifier's own code, so verifying each claim as its transaction executes reaches the verdict the block's
    /// pre-state does, except for a verifier deployed earlier in the same block.</remarks>
    /// <exception cref="InvalidBlockException">The proof is missing, does not match the header, or does not fit the dependencies.</exception>
    public static void AssignWitnesses(Block block)
    {
        // Only a header committing to a proof has one to check; a fork block's header is validated to carry the hash.
        if (block.Header.DependencyProofHash is null) return;

        byte[]? proof = block.DependencyProof;
        if (proof is null)
        {
            // A block without dependencies commits to the empty proof, which needs no carrying.
            if (block.Header.DependencyProofHash == Keccak.OfAnEmptyString && !HasClaims(block)) return;
            throw new InvalidBlockException(block, Unavailable);
        }

        if (proof.Length > Eip8141Constants.MaxDependencyProofBytes) throw new InvalidBlockException(block, TooLarge);
        if (block.Header.DependencyProofHash != Keccak.Compute(proof)) throw new InvalidBlockException(block, HashMismatch);

        Dependency[] dependencies = BlockDependencies(block, out Dependency[]?[] perTransaction);
        byte[][] witnesses = [];
        if (dependencies.Length == 0)
        {
            if (proof.Length != 0) throw new InvalidBlockException(block, Malformed);
        }
        else
        {
            if (!TryDecode(proof, out witnesses)) throw new InvalidBlockException(block, Malformed);
            if (witnesses.Length != dependencies.Length) throw new InvalidBlockException(block, WitnessCountMismatch);
        }

        for (int j = 0; j < dependencies.Length; j++)
        {
            if ((ulong)witnesses[j].Length != dependencies[j].WitnessLength) throw new InvalidBlockException(block, WitnessLengthMismatch);
        }

        Transaction[] transactions = block.Transactions;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (perTransaction[i] is not { } resolved) continue;

            byte[][] assigned = new byte[resolved.Length][];
            for (int c = 0; c < resolved.Length; c++)
            {
                assigned[c] = witnesses[Array.BinarySearch(dependencies, resolved[c])];
            }

            transactions[i].ClaimWitnesses = assigned;
        }
    }

    /// <summary>Builds the dependency proof of a block being produced from its frame transactions' witnesses, and
    /// commits the header to it.</summary>
    /// <exception cref="InvalidBlockException">A transaction lacks a witness, or the proof exceeds <c>MAX_DEPENDENCY_PROOF_BYTES</c>.</exception>
    public static void Build(Block block)
    {
        Dependency[] dependencies = BlockDependencies(block, out Dependency[]?[] perTransaction);
        byte[]?[] witnesses = new byte[dependencies.Length][];
        Transaction[] transactions = block.Transactions;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (perTransaction[i] is not { } resolved) continue;

            byte[][]? claimWitnesses = transactions[i].ClaimWitnesses;
            for (int c = 0; c < resolved.Length; c++)
            {
                if (claimWitnesses is null || c >= claimWitnesses.Length) throw new InvalidBlockException(block, MissingWitness);
                witnesses[Array.BinarySearch(dependencies, resolved[c])] ??= claimWitnesses[c];
            }
        }

        byte[] proof = Encode(witnesses!);
        if (proof.Length > Eip8141Constants.MaxDependencyProofBytes) throw new InvalidBlockException(block, TooLarge);

        block.DependencyProof = proof;
        block.Header.DependencyProofHash = Keccak.Compute(proof);
    }

    private static bool HasClaims(Block block)
    {
        foreach (Transaction tx in block.Transactions)
        {
            if (tx.Type == TxType.FrameTx && tx.FrameClaims is { Length: > 0 }) return true;
        }

        return false;
    }
}
