// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>An EIP-8518 proof wrapper <c>[transactions, deps, recursive-stark]</c>: frame transactions whose leanSPHINCS
/// witnesses one EIP-8288 recursive proof replaces.</summary>
/// <remarks>
/// Each transaction is EIP-8141's pooled encoding with the witness of every aggregatable claim emptied; the other claims keep
/// their witnesses. A wrapper is identified by the Keccak-256 of its complete canonical encoding, which
/// <see cref="ProofWrapperCodec.MaxWrapperBytes"/> bounds.
/// </remarks>
public sealed class ProofWrapper
{
    /// <summary>The aggregate transaction encodings, <c>FRAME_TX_TYPE || rlp([tx_body, wrapper_version, witnesses, blob_sidecar])</c>,
    /// in strictly ascending consensus transaction-hash order.</summary>
    public required byte[][] EncodedTransactions { get; init; }

    /// <summary>The decoded transactions, index for index with <see cref="EncodedTransactions"/>.</summary>
    /// <remarks>Once <see cref="ProofWrapperCodec.MarkCovered"/> runs, the emptied witnesses are
    /// <see cref="FrameTxClaimVerifier.AggregatedWitness"/>, which the pool admits as proven.</remarks>
    public required Transaction[] Transactions { get; init; }

    /// <summary>The canonical union of the transactions' aggregated dependencies.</summary>
    public required IReadOnlyList<AggregatedDependency> Deps { get; init; }

    public required byte[] Proof { get; init; }

    /// <summary>The complete canonical encoding.</summary>
    public required byte[] Encoded { get; init; }

    public required Hash256 Hash { get; init; }

    /// <summary>Keccak-256 of the concatenated 96-byte dependencies: EIP-8518's <c>deps-hash</c>, which is EIP-8288's
    /// <c>get_deps_hash</c>.</summary>
    public required ValueHash256 DepsHash { get; init; }

    /// <summary>Keccak-256 of the concatenated, ascending consensus transaction hashes: EIP-8518's <c>txs-hash</c>.</summary>
    public required ValueHash256 TxsHash { get; init; }

    /// <summary>The ascending consensus transaction hashes: EIP-8518's manifest.</summary>
    public required Hash256[] Manifest { get; init; }

    public int Length => Encoded.Length;
}

/// <summary>Encodes, decodes and checks EIP-8518 proof wrappers.</summary>
public static class ProofWrapperCodec
{
    /// <summary>EIP-8518 <c>MAX_WRAPPER_BYTES</c> (2 MiB).</summary>
    public const int MaxWrapperBytes = 1 << 21;

    /// <summary>The pooled encoding of an aggregate transaction: its typed envelope, with its pooled wrapper.</summary>
    public const RlpBehaviors TransactionBehaviors = RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping;

    public const string NotAggregatable = "wrapper transaction has no aggregatable claim";
    public const string CoveredWitnessRetained = "wrapper transaction retains the witness of an aggregatable claim";
    public const string UncoveredWitnessMissing = "wrapper transaction lacks the witness of a claim that is not aggregated";
    public const string DepsMismatch = "wrapper deps are not the canonical union of its transactions' aggregatable claims";
    public const string BlobsNotSupported = "wrapper transactions carrying blobs are not supported";

    private static readonly int ReferenceLength = Rlp.LengthOfSequence(Rlp.LengthOfKeccakRlp);

    /// <summary>The outcome of <see cref="TryDecode"/>, which decides how the sender is treated.</summary>
    public enum Verdict
    {
        Valid,

        /// <summary>A transport violation: discarded, and its sender may be penalized.</summary>
        Malformed,

        /// <summary>A compact reference this node holds no encoding for: retried in full, without penalty.</summary>
        CacheMiss,

        /// <summary>A dependency-rule failure: discarded, but its sender is not penalized.</summary>
        Invalid,

        /// <summary>A wrapper this node cannot use, which is not thereby invalid.</summary>
        Unsupported,
    }

    /// <summary>The aggregate encoding of a validated pooled frame transaction: its pooled encoding with the witness of every
    /// aggregatable claim emptied, or <see langword="null"/> when it has no such claim or carries blobs.</summary>
    /// <remarks>The other witnesses are kept, so the same transaction normalized from any raw signatures encodes identically.</remarks>
    public static byte[]? Normalize(Transaction tx)
    {
        if (tx.Type != TxType.FrameTx || tx.CarriesBlobs || tx.FrameClaims is not { Length: > 0 } claims
            || tx.ClaimWitnesses is not { } witnesses || witnesses.Length != claims.Length)
        {
            return null;
        }

        byte[][] normalized = new byte[claims.Length][];
        bool aggregated = false;
        for (int i = 0; i < claims.Length; i++)
        {
            bool aggregatable = Eip8288Dependencies.IsAggregatable(claims[i].Verifier);
            aggregated |= aggregatable;
            normalized[i] = aggregatable ? FrameTxClaimVerifier.AggregatedWitness : witnesses[i];
        }

        if (!aggregated) return null;

        Transaction copy = new();
        tx.CopyTo(copy);
        copy.ClaimWitnesses = normalized;
        copy.NetworkWrapper = null;
        return TxDecoder.Instance.Encode(copy, TransactionBehaviors);
    }

    /// <summary>EIP-8518's <c>encoding-hash</c>: Keccak-256 of an aggregate transaction encoding, without its RLP string prefix.</summary>
    public static ValueHash256 EncodingHash(ReadOnlySpan<byte> encodedTransaction) => ValueKeccak.Compute(encodedTransaction);

    /// <summary>The aggregated dependencies of <paramref name="tx"/>'s claims, unsorted and with repetitions.</summary>
    public static List<AggregatedDependency> AggregatedDependencies(Transaction tx)
    {
        List<AggregatedDependency> dependencies = [];
        foreach (FrameTxDependencyProof.Dependency dependency in FrameTxDependencyProof.Resolve(tx))
        {
            if (AggregatedDependencyProof.IsAggregated(dependency)) dependencies.Add(Eip8288Dependencies.Map(dependency.Key, dependency.Msg));
        }

        return dependencies;
    }

    /// <summary>Builds the canonical wrapper of <paramref name="encodedTransactions"/>, already in ascending transaction-hash
    /// order, proven by <paramref name="proof"/>.</summary>
    /// <exception cref="ArgumentException">The encoding exceeds <see cref="MaxWrapperBytes"/>.</exception>
    public static ProofWrapper Create(IReadOnlyList<byte[]> encodedTransactions, IReadOnlyList<Transaction> transactions,
        IReadOnlyList<AggregatedDependency> deps, byte[] proof)
    {
        byte[] encoded = Encode(encodedTransactions, deps, proof);
        if (encoded.Length > MaxWrapperBytes) throw new ArgumentException("Proof wrapper exceeds MAX_WRAPPER_BYTES.", nameof(encodedTransactions));

        Hash256[] manifest = new Hash256[transactions.Count];
        for (int i = 0; i < manifest.Length; i++) manifest[i] = transactions[i].Hash!;
        return new ProofWrapper
        {
            EncodedTransactions = [.. encodedTransactions],
            Transactions = [.. transactions],
            Deps = deps,
            Proof = proof,
            Encoded = encoded,
            Hash = Keccak.Compute(encoded),
            DepsHash = Eip8288Dependencies.ComputeDepsHash(deps),
            TxsHash = ComputeTxsHash(manifest),
            Manifest = manifest,
        };
    }

    /// <summary>EIP-8518's <c>txs-hash</c> of a manifest.</summary>
    public static ValueHash256 ComputeTxsHash(IReadOnlyList<Hash256> manifest)
    {
        KeccakHash hash = KeccakHash.Create();
        foreach (Hash256 txHash in manifest) hash.Update(txHash.Bytes);
        return hash.GenerateValueHash();
    }

    /// <summary>Encodes <c>[transactions, deps, recursive-stark]</c>.</summary>
    /// <param name="reference">Selects the transactions written as <c>[encoding-hash]</c> references, for a compact response;
    /// <see langword="null"/> for the canonical encoding.</param>
    public static byte[] Encode(IReadOnlyList<byte[]> encodedTransactions, IReadOnlyList<AggregatedDependency> deps, ReadOnlySpan<byte> proof,
        Func<byte[], bool>? reference = null)
    {
        bool[] references = new bool[encodedTransactions.Count];
        int txsLength = 0;
        for (int i = 0; i < references.Length; i++)
        {
            byte[] transaction = encodedTransactions[i];
            // A reference is used only where it is smaller, as EIP-8518 asks.
            references[i] = reference is not null && Rlp.LengthOf(transaction) > ReferenceLength && reference(transaction);
            txsLength += references[i] ? ReferenceLength : Rlp.LengthOf(transaction);
        }

        int depsLength = deps.Count * Rlp.LengthOfByteString(Eip8288Constants.DependencyTripleLength, 0);
        int contentLength = Rlp.LengthOfSequence(txsLength) + Rlp.LengthOfSequence(depsLength) + Rlp.LengthOf(proof);

        byte[] encoded = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(encoded);
        RlpSequence outer = writer.StartSequence(contentLength);
        RlpSequence txs = writer.StartSequence(txsLength);
        for (int i = 0; i < references.Length; i++)
        {
            if (!references[i])
            {
                writer.Write(encodedTransactions[i]);
                continue;
            }

            RlpSequence referenceSequence = writer.StartSequence(Rlp.LengthOfKeccakRlp);
            writer.Encode(EncodingHash(encodedTransactions[i]));
            writer.EndSequence(referenceSequence);
        }

        writer.EndSequence(txs);
        RlpSequence depsSequence = writer.StartSequence(depsLength);
        Span<byte> triple = stackalloc byte[Eip8288Constants.DependencyTripleLength];
        foreach (AggregatedDependency dependency in deps)
        {
            dependency.WriteTo(triple);
            writer.Write(triple);
        }

        writer.EndSequence(depsSequence);
        writer.Write(proof);
        writer.EndSequence(outer);
        return encoded;
    }

    /// <summary>Decodes one wrapper item and checks it against its transactions' claims, short of verifying its proof.</summary>
    /// <param name="item">The wrapper's RLP item as received.</param>
    /// <param name="resolve">Resolves a compact <c>[encoding-hash]</c> reference to a cached aggregate transaction encoding,
    /// or <see langword="null"/> when references are not allowed, as in a <c>compact = 0</c> response.</param>
    /// <param name="maxLength">The announced length the reconstructed wrapper may not exceed.</param>
    public static Verdict TryDecode(ReadOnlySpan<byte> item, Func<ValueHash256, byte[]?>? resolve, int maxLength,
        out ProofWrapper? wrapper, out string? error)
    {
        wrapper = null;
        error = null;
        List<byte[]> encodedTransactions = [];
        List<Transaction> transactions = [];
        byte[] rawDeps;
        byte[] proof;
        int bound = Math.Min(maxLength, MaxWrapperBytes);
        try
        {
            RlpReader reader = new(item);
            RlpReader content = reader.EnterSequence(out int contentLength);
            RlpReader txs = content.EnterSequence(out int txsLength);
            int reconstructed = 0;
            Hash256? previous = null;
            while (txs.HasNext)
            {
                byte[] encoded;
                if (txs.IsNextSequence)
                {
                    if (resolve is null) return Fail(Verdict.Malformed, "reference in a full response", out error);
                    RlpReader reference = txs.EnterSequence(out int referenceLength);
                    ValueHash256 encodingHash = new(reference.ReadBytes(ValueHash256.MemorySize));
                    txs.ExitSequence(reference, referenceLength);
                    if (resolve(encodingHash) is not { } cached) return Fail(Verdict.CacheMiss, "unresolved encoding reference", out error);
                    encoded = cached;
                }
                else
                {
                    encoded = txs.ReadBytes().ToArray();
                }

                // The complete wrapper determines the byte bound, so reconstruction stops as soon as it would cross it.
                reconstructed += Rlp.LengthOf(encoded);
                if (reconstructed > bound) return Fail(Verdict.Malformed, "reconstructed wrapper exceeds its bound", out error);

                Transaction transaction = DecodeTransaction(encoded);
                if (previous is not null && previous.CompareTo(transaction.Hash!) >= 0)
                    return Fail(Verdict.Malformed, "wrapper transactions are not in strictly ascending hash order", out error);
                previous = transaction.Hash;
                encodedTransactions.Add(encoded);
                transactions.Add(transaction);
            }

            content.ExitSequence(txs, txsLength);
            RlpReader depsReader = content.EnterSequence(out int depsLength);
            rawDeps = new byte[depsLength];
            int offset = 0;
            while (depsReader.HasNext)
            {
                depsReader.ReadBytes(Eip8288Constants.DependencyTripleLength).CopyTo(rawDeps.AsSpan(offset));
                offset += Eip8288Constants.DependencyTripleLength;
            }

            content.ExitSequence(depsReader, depsLength);
            proof = content.ReadBytes().ToArray();
            reader.ExitSequence(content, contentLength);
            if (reader.HasNext) return Fail(Verdict.Malformed, "trailing bytes after the wrapper", out error);
            Array.Resize(ref rawDeps, offset);
        }
        catch (Exception exception) when (exception is RlpReaderException)
        {
            return Fail(Verdict.Malformed, exception.Message, out error);
        }

        if (transactions.Count == 0) return Fail(Verdict.Malformed, "wrapper carries no transaction", out error);
        if (proof.Length is 0 or > Eip8288Constants.MaxAggregateProofBytes) return Fail(Verdict.Invalid, "wrapper proof is empty or too large", out error);

        List<AggregatedDependency> expected = [];
        foreach (Transaction transaction in transactions)
        {
            Verdict verdict = CheckClaims(transaction, expected, out error);
            if (verdict != Verdict.Valid) return verdict;
        }

        List<AggregatedDependency> deps = Eip8288Dependencies.Canonicalize(expected);
        if (!Matches(deps, rawDeps)) return Fail(Verdict.Invalid, DepsMismatch, out error);

        ProofWrapper created = Create(encodedTransactions, transactions, deps, proof);
        if (created.Length > bound) return Fail(Verdict.Malformed, "reconstructed wrapper exceeds its bound", out error);
        wrapper = created;
        return Verdict.Valid;
    }

    /// <summary>Verifies <paramref name="wrapper"/>'s recursive proof against its exact dependency union.</summary>
    public static bool VerifyProof(ProofWrapper wrapper, ILeanProofVerifier verifier) =>
        verifier.VerifyDependencyProof(wrapper.DepsHash, Eip8288Constants.AggregatedVk, wrapper.Proof);

    /// <summary>Marks the emptied witnesses of a verified wrapper's transactions as proven by its proof.</summary>
    public static void MarkCovered(ProofWrapper wrapper)
    {
        foreach (Transaction transaction in wrapper.Transactions)
        {
            TxFrameClaim[] claims = transaction.FrameClaims!;
            byte[][] witnesses = transaction.ClaimWitnesses!;
            for (int i = 0; i < claims.Length; i++)
            {
                if (Eip8288Dependencies.IsAggregatable(claims[i].Verifier)) witnesses[i] = FrameTxClaimVerifier.AggregatedWitness;
            }
        }
    }

    private static Transaction DecodeTransaction(byte[] encoded)
    {
        Transaction transaction;
        try
        {
            transaction = TxDecoder.Instance.DecodeCompleteNotNull(encoded, TransactionBehaviors);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new RlpReaderException("wrapper transaction is not a pooled transaction", exception);
        }

        transaction.Hash ??= transaction.CalculateHash();
        return transaction;
    }

    private static Verdict CheckClaims(Transaction transaction, List<AggregatedDependency> deps, out string? error)
    {
        error = null;
        if (transaction.Type != TxType.FrameTx || transaction.FrameClaims is not { Length: > 0 } claims)
            return Fail(Verdict.Invalid, NotAggregatable, out error);
        if (transaction.CarriesBlobs) return Fail(Verdict.Unsupported, BlobsNotSupported, out error);
        if (transaction.ClaimWitnesses is not { } witnesses || witnesses.Length != claims.Length)
            return Fail(Verdict.Invalid, FrameTxClaimVerifier.WitnessCountMismatch, out error);

        int before = deps.Count;
        for (int i = 0; i < claims.Length; i++)
        {
            if (Eip8288Dependencies.IsAggregatable(claims[i].Verifier))
            {
                if (witnesses[i].Length != 0) return Fail(Verdict.Invalid, CoveredWitnessRetained, out error);
            }
            else if ((ulong)witnesses[i].Length != claims[i].ResolvedWitnessLength)
            {
                return Fail(Verdict.Invalid, UncoveredWitnessMissing, out error);
            }
        }

        deps.AddRange(AggregatedDependencies(transaction));
        return deps.Count == before ? Fail(Verdict.Invalid, NotAggregatable, out error) : Verdict.Valid;
    }

    private static bool Matches(List<AggregatedDependency> deps, ReadOnlySpan<byte> rawDeps)
    {
        if (rawDeps.Length != deps.Count * Eip8288Constants.DependencyTripleLength) return false;
        Span<byte> triple = stackalloc byte[Eip8288Constants.DependencyTripleLength];
        for (int i = 0; i < deps.Count; i++)
        {
            deps[i].WriteTo(triple);
            if (!triple.SequenceEqual(rawDeps.Slice(i * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength))) return false;
        }

        return true;
    }

    private static Verdict Fail(Verdict verdict, string message, out string? error)
    {
        error = message;
        return verdict;
    }
}
