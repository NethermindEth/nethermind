// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
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
    public const string VerifierNotDeployed = "custom claim verifier has no code in the block's pre-state";
    public const string VerifierGasExceedsBlockGasLimit = "dependency verification exceeds the block gas limit";

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
    /// pre-state does once <see cref="CheckVerifiersDeployed"/> has ruled out a verifier deployed earlier in the same block.</remarks>
    /// <param name="spec">The block's spec; an EIP-8288 spec also accepts an aggregated proof, whose leanSPHINCS
    /// claims are then assigned <see cref="FrameTxClaimVerifier.AggregatedWitness"/>.</param>
    /// <param name="leanProofVerifier">Verifies an aggregated proof's recursive STARK.</param>
    /// <exception cref="InvalidBlockException">The proof does not match the header, or does not fit the dependencies.</exception>
    /// <exception cref="InvalidOperationException">The proof is not available, or its STARK cannot be verified here, so no
    /// verdict can be reached.</exception>
    public static void AssignWitnesses(Block block, IReleaseSpec? spec = null, ILeanProofVerifier? leanProofVerifier = null)
    {
        // Only a header committing to a proof has one to check; a fork block's header is validated to carry the hash.
        if (block.Header.DependencyProofHash is null) return;

        byte[]? proof = block.DependencyProof;
        if (proof is null)
        {
            // A block without dependencies commits to the empty proof, which needs no carrying.
            if (block.Header.DependencyProofHash == Keccak.OfAnEmptyString && !HasClaims(block)) return;
            // The proof travels only with newPayload and is not stored, so a block read back from the database has
            // none; that is no fault of the block, which must not be marked invalid for it.
            throw new InvalidOperationException($"{Unavailable} for block {block.ToString(Block.Format.Short)}");
        }

        if (proof.Length > Eip8141Constants.MaxDependencyProofBytes) throw new InvalidBlockException(block, TooLarge);
        if (block.Header.DependencyProofHash != Keccak.Compute(proof)) throw new InvalidBlockException(block, HashMismatch);

        Dependency[] dependencies = BlockDependencies(block, out Dependency[]?[] perTransaction);
        byte[][] witnesses = [];
        if (dependencies.Length == 0)
        {
            if (proof.Length != 0) throw new InvalidBlockException(block, Malformed);
        }
        // The zkVM guests carry no dependency proof, so they leave EIP-8288 verification out of their image.
        else if (!ZkEvmFlag.IsActive && proof is [Eip8288Constants.DependencyProofVersionAggregated, ..] && spec?.IsEip8288Enabled == true)
        {
            witnesses = AggregatedDependencyProof.Verify(block, dependencies, proof,
                leanProofVerifier ?? throw new InvalidOperationException($"No EIP-8288 verifier to check block {block.ToString(Block.Format.Short)}"));
        }
        else
        {
            // Every claim charges its full gas limit to its transaction, so a valid block never needs more verifier
            // gas than its own gas limit; checked before any verifier runs, as a bound on their work.
            if (ExceedsGasLimit(dependencies, block.Header.GasLimit)) throw new InvalidBlockException(block, VerifierGasExceedsBlockGasLimit);
            if (!TryDecode(proof, out witnesses)) throw new InvalidBlockException(block, Malformed);
            if (witnesses.Length != dependencies.Length) throw new InvalidBlockException(block, WitnessCountMismatch);
        }

        for (int j = 0; j < dependencies.Length; j++)
        {
            if ((ulong)witnesses[j].Length != dependencies[j].WitnessLength && !ReferenceEquals(witnesses[j], FrameTxClaimVerifier.AggregatedWitness))
                throw new InvalidBlockException(block, WitnessLengthMismatch);
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

    /// <summary>Checks every custom verifier <paramref name="block"/>'s claims name is a precompile or carries
    /// non-delegated code in <paramref name="preState"/>.</summary>
    /// <remarks>EIP-8141 verifies every dependency against the block's pre-state. A verifier the pre-state lacks fails
    /// there, and deployed code is immutable (EIP-6780), so with this check verifying each claim as its transaction
    /// executes reaches the pre-state verdict.</remarks>
    /// <exception cref="InvalidBlockException">A custom verifier has no code, or delegated code, in the pre-state.</exception>
    public static void CheckVerifiersDeployed(Block block, IReadOnlyStateProvider preState, IReleaseSpec spec)
    {
        foreach (Transaction tx in block.Transactions)
        {
            if (tx.Type != TxType.FrameTx) continue;

            foreach (TxFrameClaim claim in tx.FrameClaims ?? [])
            {
                Address verifier = claim.Verifier;
                if (claim.IsProtocolVerifier || spec.IsPrecompile(verifier)) continue;
                if (!preState.IsContract(verifier) || preState.IsDelegatedCode(verifier)) throw new InvalidBlockException(block, VerifierNotDeployed);
            }
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

    /// <summary>The dependency proof a block being produced accumulates, so a transaction that would leave it
    /// unbuildable is skipped rather than failing the whole build.</summary>
    internal sealed class Budget
    {
        private readonly HashSet<Dependency> _included = [];
        private int _contentLength;

        /// <summary>Whether <paramref name="tx"/>'s witnesses fit beside those already included.</summary>
        /// <param name="added">The dependencies <paramref name="tx"/> adds, to pass to <see cref="Include"/> once it is in the block.</param>
        /// <param name="error">Why the transaction does not fit, or <see langword="null"/>.</param>
        public bool TryFit(Transaction tx, out Dependency[] added, out int addedLength, out string? error)
        {
            added = [];
            addedLength = 0;
            error = null;
            TxFrameClaim[] claims = tx.FrameClaims ?? [];
            if (claims.Length == 0) return true;

            byte[][]? witnesses = tx.ClaimWitnesses;
            Dependency[] resolved = Resolve(tx);
            HashSet<Dependency> fresh = [];
            for (int c = 0; c < resolved.Length; c++)
            {
                if (witnesses is null || c >= witnesses.Length || (ulong)witnesses[c].Length != resolved[c].WitnessLength)
                {
                    error = MissingWitness;
                    return false;
                }

                if (!_included.Contains(resolved[c]) && fresh.Add(resolved[c])) addedLength += Rlp.LengthOf(witnesses[c]);
            }

            if (1 + Rlp.LengthOfSequence(_contentLength + addedLength) > Eip8141Constants.MaxDependencyProofBytes)
            {
                error = TooLarge;
                return false;
            }

            added = [.. fresh];
            return true;
        }

        /// <summary>Records the dependencies a transaction added once it is in the block.</summary>
        public void Include(Dependency[] added, int addedLength)
        {
            _included.UnionWith(added);
            _contentLength += addedLength;
        }
    }

    private static bool ExceedsGasLimit(Dependency[] dependencies, ulong gasLimit)
    {
        foreach (Dependency dependency in dependencies)
        {
            if (dependency.GasLimit > gasLimit) return true;
            gasLimit -= dependency.GasLimit;
        }

        return false;
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
