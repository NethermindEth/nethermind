// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary>
/// A single EIP-8288 dependency triple <c>(scheme, data_hash, verification_key)</c> declared by a
/// dependency-verification frame. <c>data_hash</c>/<c>verification_key</c> are the message hash and
/// 32-byte public key (leanSPHINCS) or the public commitment and program key hash (leanSTARK).
/// </summary>
public readonly struct FrameDependency(byte scheme, ValueHash256 dataHash, ValueHash256 verificationKey)
    : IEquatable<FrameDependency>
{
    public byte Scheme { get; } = scheme;
    public ValueHash256 DataHash { get; } = dataHash;
    public ValueHash256 VerificationKey { get; } = verificationKey;

    /// <summary>Per-scheme verification gas (spec <c>LEANSPHINCS/LEANSTARK_VERIFICATION_GAS</c>).</summary>
    public ulong VerificationGas => Scheme == Eip8288Constants.LeanStarkScheme
        ? Eip8288Constants.LeanStarkVerificationGas
        : Eip8288Constants.LeanSphincsVerificationGas;

    /// <summary>Per-scheme frame charge from the batch-pricing EIP's activation (ethereum/EIPs#12664), which replaces
    /// <see cref="VerificationGas"/>.</summary>
    public ulong BatchPricedVerificationGas => Scheme switch
    {
        Eip8288Constants.LeanStarkScheme => Eip8288Constants.BatchPricedLeanStarkVerificationGas,
        Eip8288Constants.BatchedLeanStarkScheme => Eip8288Constants.LeanStarkClaimGas,
        // A leaf's verification and its proof share are charged once, to its payment.
        Eip8288Constants.LeanStarkLeafPaymentScheme => Eip8288Constants.BatchPricedLeanStarkVerificationGas + Eip8288Constants.RecursiveProofShareGas,
        _ => Eip8288Constants.BatchPricedLeanSphincsVerificationGas
    };

    /// <summary>The frame charge <see cref="VerificationGas"/>, or <see cref="BatchPricedVerificationGas"/> once
    /// <paramref name="batchPricing"/> is active.</summary>
    public ulong FrameCharge(bool batchPricing) => batchPricing ? BatchPricedVerificationGas : VerificationGas;

    /// <summary>Writes the canonical 96-byte encoding <c>bytes32_be(scheme) || data_hash || verification_key</c>.</summary>
    public void WriteTo(Span<byte> destination)
    {
        // Only the scheme word's leading padding needs clearing; the two hashes overwrite bytes 32..96.
        destination[..31].Clear();
        destination[31] = Scheme;
        DataHash.Bytes.CopyTo(destination[32..64]);
        VerificationKey.Bytes.CopyTo(destination[64..96]);
    }

    public bool Equals(FrameDependency other) =>
        Scheme == other.Scheme && DataHash == other.DataHash && VerificationKey == other.VerificationKey;

    public override bool Equals(object? obj) => obj is FrameDependency other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Scheme, DataHash, VerificationKey);
}

/// <summary>
/// Parses and aggregates EIP-8288 dependencies from frames, transactions, and blocks, and computes
/// the block-level dependency commitment.
/// </summary>
public static class Eip8288Dependencies
{
    public static bool IsDependencyFrame(TxFrame frame) => frame.Mode == FrameMode.DepVerify;

    /// <summary>Whether <paramref name="transaction"/> has at least one dependency verification frame, even one carrying no
    /// triples, so <c>dependencies(tx) == []</c> alone does not make it dependency-free.</summary>
    /// <remarks>EIP-8288 and EIP-8437: such transactions are never independently announced or broadcast, are admitted only with
    /// a fully validated covering wrapper, and an inclusion list obligates them only under a verified covering proof.</remarks>
    public static bool IsDependencyBearing(Transaction transaction)
    {
        if (transaction.PersistedDependencyBearing is { } persisted) return persisted;
        TxFrame[]? frames = transaction.Frames;
        if (frames is null) return false;
        foreach (TxFrame frame in frames)
            if (IsDependencyFrame(frame)) return true;
        return false;
    }

    /// <summary>The <c>data_triples</c> of a dependency frame, parsed from its 96·k-byte <c>data</c>.</summary>
    public static IEnumerable<FrameDependency> ParseFrame(TxFrame frame)
    {
        ReadOnlyMemory<byte> data = frame.Data;
        int count = data.Length / Eip8288Constants.DependencyTripleLength;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> triple = data.Span.Slice(i * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength);
            yield return new FrameDependency(triple[31], new ValueHash256(triple[32..64]), new ValueHash256(triple[64..96]));
        }
    }

    /// <summary>Spec <c>dependencies(tx)</c>: resolves zero data hashes to the containing transaction's signature hash,
    /// then sorts and deduplicates the triples without changing the frame data.</summary>
    /// <param name="computeSignatureHash">The EIP-8141 canonical encoder, evaluated once only when a zero hash is declared.</param>
    public static List<FrameDependency> ForTransaction(Transaction tx, Func<Transaction, ValueHash256> computeSignatureHash) =>
        Canonicalize(Declarations(tx, computeSignatureHash));

    private static IEnumerable<FrameDependency> Declarations(Transaction tx, Func<Transaction, ValueHash256> computeSignatureHash)
    {
        ValueHash256? signatureHash = null;
        TxFrame[]? frames = tx.Frames;
        if (frames is null) yield break;

        foreach (TxFrame frame in frames)
        {
            if (!IsDependencyFrame(frame)) continue;
            foreach (FrameDependency dep in ParseFrame(frame))
            {
                yield return dep.DataHash == default
                    ? new FrameDependency(dep.Scheme, signatureHash ??= computeSignatureHash(tx), dep.VerificationKey)
                    : dep;
            }
        }
    }

    /// <summary>Spec <c>dependencies(block)</c>: sorted, deduplicated transaction dependencies.</summary>
    /// <remarks>Walks the frames directly rather than through the iterators, resolving zero data hashes once per transaction.</remarks>
    public static List<FrameDependency> ForBlock(Block block, Func<Transaction, ValueHash256> computeSignatureHash)
    {
        List<FrameDependency> dependencies = [];
        foreach (Transaction tx in block.Transactions)
        {
            ValueHash256? signatureHash = null;
            TxFrame[]? frames = tx.Frames;
            if (frames is null) continue;

            foreach (TxFrame frame in frames)
            {
                if (!IsDependencyFrame(frame)) continue;

                ReadOnlySpan<byte> data = frame.Data.Span;
                int count = data.Length / Eip8288Constants.DependencyTripleLength;
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> triple = data.Slice(i * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength);
                    ValueHash256 dataHash = new(triple[32..64]);
                    if (dataHash == default) dataHash = signatureHash ??= computeSignatureHash(tx);
                    dependencies.Add(new FrameDependency(triple[31], dataHash, new ValueHash256(triple[64..96])));
                }
            }
        }

        return Canonicalize(dependencies);
    }

    /// <summary>
    /// The EIP-8288 <c>recursive_stark_gas</c> of this transaction: part of its intrinsic gas, charged per
    /// declared dependency, duplicates included, on top of the per-scheme verification gas the frames already paid.
    /// </summary>
    /// <remarks>EIP-8288 charges <see cref="Eip8288Constants.LeanStarkVerificationGas"/> per declaration. From the batch-pricing EIP's
    /// activation (ethereum/EIPs#12664), when <paramref name="batchedEnabled"/>, it charges
    /// <see cref="Eip8288Constants.RecursiveProofShareGas"/> instead, and neither batch claims nor leaf payments pay it: their frame
    /// charges replace it.</remarks>
    public static ulong RecursiveStarkGas(Transaction tx, bool batchedEnabled = false)
    {
        TxFrame[]? frames = tx.Frames;
        if (frames is null) return 0;

        ulong count = 0;
        foreach (TxFrame frame in frames)
        {
            if (!IsDependencyFrame(frame)) continue;
            ReadOnlySpan<byte> data = frame.Data.Span;
            // Whole triples only, as Length / 96 counts them; i is each one's scheme byte.
            for (int i = 31; i + 64 < data.Length; i += Eip8288Constants.DependencyTripleLength)
            {
                if (!batchedEnabled || data[i] is not (Eip8288Constants.BatchedLeanStarkScheme or Eip8288Constants.LeanStarkLeafPaymentScheme)) count++;
            }
        }

        return count * (batchedEnabled ? Eip8288Constants.RecursiveProofShareGas : Eip8288Constants.LeanStarkVerificationGas);
    }

    /// <summary>Concatenates dependencies into their <c>96·k</c>-byte wire form.</summary>
    public static byte[] Serialize(IReadOnlyList<FrameDependency> dependencies)
    {
        byte[] buffer = new byte[dependencies.Count * Eip8288Constants.DependencyTripleLength];
        for (int i = 0; i < dependencies.Count; i++)
        {
            dependencies[i].WriteTo(buffer.AsSpan(i * Eip8288Constants.DependencyTripleLength));
        }

        return buffer;
    }

    /// <summary>Parses a <c>96·k</c>-byte concatenation of triples back into dependencies.</summary>
    public static List<FrameDependency> Parse(ReadOnlySpan<byte> data)
    {
        int count = data.Length / Eip8288Constants.DependencyTripleLength;
        List<FrameDependency> dependencies = new(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> triple = data.Slice(i * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength);
            dependencies.Add(new FrameDependency(triple[31], new ValueHash256(triple[32..64]), new ValueHash256(triple[64..96])));
        }

        return dependencies;
    }

    // Closes the digest of a list holding leanSTARK dependencies: a 96-byte preimage, which no list of 64-byte entries shares.
    private static ReadOnlySpan<byte> LeanStarkDepsDomain => "eip8288/leanstark-deps\0\0\0\0\0\0\0\0\0\0"u8;

    private static ReadOnlySpan<byte> BatchFinalTag => "EIP8288:batch-final:v1"u8;

    /// <summary>Spec <c>get_deps_hash</c>, the <c>block_deps_hash</c> and <c>deps_hash</c> a recursive STARK is verified against:
    /// <c>DEPS_HASH</c> over the canonical dependencies' 64-byte <c>data_hash || key</c> entries, where a leanSPHINCS key is the raw
    /// public key a triple carries in its <c>verification_key_hash</c> field, not a hash of it.</summary>
    /// <remarks>EIP-8288 leaves <c>DEPS_HASH</c> TBD; this uses Keccak-256, the hash the pinned recursive guest commits its
    /// dependency digest with. A leanSPHINCS-only list hashes exactly as the EIP specifies. The EIP reserves leanSTARK until
    /// <c>get_deps_hash</c> is defined for it; as a deviation, a list holding any non-leanSPHINCS dependency (only leanSTARK passes
    /// validation) commits to
    /// <c>DEPS_HASH(get_deps_hash(leanSPHINCS) || get_deps_hash(leanSTARK) || "eip8288/leanstark-deps")</c>, the domain
    /// zero-padded to 32 bytes, as the pinned recursive guest does. That 96-byte preimage is never a list of 64-byte entries,
    /// and the position of an entry's list binds its scheme. The batch-pricing EIP's schemes (ethereum/EIPs#12664) join the
    /// leanSTARK list keyed by <see cref="SchemeKey"/>, sorted by entry; a block holding either is proved against
    /// <see cref="ProofStatement"/>.</remarks>
    public static ValueHash256 ComputeDepsHash(IReadOnlyList<FrameDependency> dependencies)
    {
        dependencies = Canonicalize(dependencies);
        ValueHash256 sphincs = ListDepsHash(dependencies, sphincs: true);
        // Canonical order sorts by scheme first, so the ends tell whether every entry is leanSPHINCS.
        if (dependencies.Count == 0 || (dependencies[0].Scheme == Eip8288Constants.LeanSphincsScheme
            && dependencies[^1].Scheme == Eip8288Constants.LeanSphincsScheme)) return sphincs;

        // Every other scheme joins the leanSTARK list, so no entry escapes the digest; validation rejects unknown schemes.
        Span<byte> joined = stackalloc byte[96];
        sphincs.Bytes.CopyTo(joined);
        ListDepsHash(dependencies, sphincs: false).Bytes.CopyTo(joined[32..]);
        LeanStarkDepsDomain.CopyTo(joined[64..]);
        return ValueKeccak.Compute(joined);
    }

    private static ValueHash256 ListDepsHash(IReadOnlyList<FrameDependency> dependencies, bool sphincs)
    {
        KeccakHash hash = KeccakHash.Create();
        if (sphincs || !HasBatchScheme(dependencies))
        {
            foreach (FrameDependency dependency in dependencies)
            {
                if ((dependency.Scheme == Eip8288Constants.LeanSphincsScheme) != sphincs) continue;
                hash.Update(dependency.DataHash.Bytes);
                hash.Update(dependency.VerificationKey.Bytes);
            }

            return hash.GenerateValueHash();
        }

        List<(ValueHash256 DataHash, ValueHash256 Key)> entries = new(dependencies.Count);
        foreach (FrameDependency dependency in dependencies)
            if (dependency.Scheme != Eip8288Constants.LeanSphincsScheme) entries.Add((dependency.DataHash, SchemeKey(dependency)));
        // Rekeyed entries leave the canonical triple order, so the entries are sorted themselves.
        entries.Sort(static (left, right) =>
        {
            int comparison = left.DataHash.CompareTo(right.DataHash);
            return comparison != 0 ? comparison : left.Key.CompareTo(right.Key);
        });
        foreach ((ValueHash256 dataHash, ValueHash256 key) in entries)
        {
            hash.Update(dataHash.Bytes);
            hash.Update(key.Bytes);
        }

        return hash.GenerateValueHash();
    }

    /// <summary>The leanSTARK list key of a dependency: its verification key, or for a batch-pricing EIP scheme
    /// <c>keccak256(scheme word || verification_key_hash)</c>, binding the scheme.</summary>
    /// <remarks>Such a preimage starts with zero bytes and a program key's with the leanVM tag, so it is no program's key.</remarks>
    private static ValueHash256 SchemeKey(in FrameDependency dependency)
    {
        if (dependency.Scheme is not (Eip8288Constants.BatchedLeanStarkScheme or Eip8288Constants.LeanStarkLeafPaymentScheme))
            return dependency.VerificationKey;
        Span<byte> preimage = stackalloc byte[64];
        preimage[31] = dependency.Scheme;
        dependency.VerificationKey.Bytes.CopyTo(preimage[32..]);
        return ValueKeccak.Compute(preimage);
    }

    /// <summary>The statement a block proof over <paramref name="dependencies"/>, of digest <paramref name="depsHash"/>, verifies
    /// against: ethereum/EIPs#12664 <c>get_batch_final_hash(depsHash)</c> when they hold a batch claim or leaf payment, else
    /// <paramref name="depsHash"/> itself.</summary>
    /// <remarks>Only a finalized proof, whose batch claims the guest checks are paid, has the former; a plain proof never
    /// satisfies a block with either scheme.</remarks>
    public static ValueHash256 ProofStatement(IReadOnlyList<FrameDependency> dependencies, in ValueHash256 depsHash) =>
        HasBatchScheme(dependencies) ? BatchFinalHash(depsHash) : depsHash;

    /// <summary>ethereum/EIPs#12664 <c>get_batch_final_hash(deps_hash) = DEPS_HASH("EIP8288:batch-final:v1" || deps_hash)</c>.</summary>
    public static ValueHash256 BatchFinalHash(in ValueHash256 depsHash)
    {
        Span<byte> preimage = stackalloc byte[BatchFinalTag.Length + 32];
        BatchFinalTag.CopyTo(preimage);
        depsHash.Bytes.CopyTo(preimage[BatchFinalTag.Length..]);
        return ValueKeccak.Compute(preimage);
    }

    private static bool HasBatchScheme(IReadOnlyList<FrameDependency> dependencies)
    {
        foreach (FrameDependency dependency in dependencies)
            if (dependency.Scheme is Eip8288Constants.BatchedLeanStarkScheme or Eip8288Constants.LeanStarkLeafPaymentScheme) return true;
        return false;
    }

    /// <summary>Whether a dependency scheme is enabled: leanSPHINCS, and leanSTARK, which this node enables as a deviation from
    /// EIP-8288 (see <see cref="ComputeDepsHash"/>).</summary>
    public static bool IsAcceptedScheme(byte scheme) => scheme is Eip8288Constants.LeanSphincsScheme or Eip8288Constants.LeanStarkScheme;

    /// <summary>Whether a dependency scheme is one EIP-8288 defines.</summary>
    public static bool IsKnownScheme(byte scheme) => scheme is Eip8288Constants.LeanSphincsScheme or Eip8288Constants.LeanStarkScheme;

    /// <summary>Whether a dependency frame may declare <paramref name="triple"/>'s scheme: an enabled EIP-8288 one, or, when
    /// <paramref name="batchedEnabled"/>, a batch-pricing EIP batch claim or leaf payment (ethereum/EIPs#12664), a payment with a
    /// nonzero digest. The caller checks the 31 zero padding bytes.</summary>
    internal static bool IsFrameScheme(ReadOnlySpan<byte> triple, bool batchedEnabled) => triple[31] switch
    {
        Eip8288Constants.BatchedLeanStarkScheme => batchedEnabled,
        Eip8288Constants.LeanStarkLeafPaymentScheme => batchedEnabled && !triple[32..64].IsZero(),
        byte scheme => IsAcceptedScheme(scheme)
    };

    /// <summary>Whether <paramref name="transaction"/> declares a batch-pricing EIP batch claim or leaf payment.</summary>
    /// <remarks>Such transactions are submitted directly to builders: they never enter or propagate through the public mempool,
    /// inside wrappers included, and impose no EIP-7805 inclusion obligation (ethereum/EIPs#12664).</remarks>
    public static bool DeclaresBatched(Transaction transaction)
    {
        foreach (TxFrame frame in transaction.Frames ?? [])
        {
            if (!IsDependencyFrame(frame)) continue;
            ReadOnlySpan<byte> data = frame.Data.Span;
            for (int i = 31; i < data.Length; i += Eip8288Constants.DependencyTripleLength)
                if (data[i] is Eip8288Constants.BatchedLeanStarkScheme or Eip8288Constants.LeanStarkLeafPaymentScheme) return true;
        }

        return false;
    }

    /// <summary>The leanSTARK dependencies in <paramref name="dependencies"/>, repeats included.</summary>
    public static int CountLeanStark(IEnumerable<FrameDependency> dependencies)
    {
        int count = 0;
        foreach (FrameDependency dependency in dependencies)
            if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) count++;
        return count;
    }

    /// <summary>Whether one dependency list of the pinned recursive guest can hold <paramref name="dependencies"/>: at most
    /// <see cref="Eip8288Constants.MaxProofDependencies"/>, and without <paramref name="batchPricing"/> at most
    /// <see cref="Eip8288Constants.MaxGenericStarkProofs"/> of them leanSTARK.</summary>
    /// <remarks>The circuit's dependency-list limit: a statement over it is split or deferred when built and a mode-1 proof over it
    /// is invalid when received (EIP-8288).</remarks>
    public static bool FitsProofList(IReadOnlyCollection<FrameDependency> dependencies, bool batchPricing) =>
        dependencies.Count <= Eip8288Constants.MaxProofDependencies
        && (batchPricing || CountLeanStark(dependencies) <= Eip8288Constants.MaxGenericStarkProofs);

    /// <summary>Whether every dependency's scheme is enabled (<see cref="IsAcceptedScheme"/>).</summary>
    public static bool AreSchemesEnabled(IReadOnlyList<FrameDependency> dependencies)
    {
        for (int i = 0; i < dependencies.Count; i++)
            if (!IsAcceptedScheme(dependencies[i].Scheme)) return false;
        return true;
    }

    /// <summary>Sorts and deduplicates dependencies for the EIP-8288 commitment.</summary>
    public static List<FrameDependency> Canonicalize(IEnumerable<FrameDependency> dependencies)
    {
        List<FrameDependency> result = [.. dependencies];
        result.Sort(static (left, right) =>
        {
            int comparison = left.Scheme.CompareTo(right.Scheme);
            if (comparison != 0) return comparison;
            comparison = left.DataHash.CompareTo(right.DataHash);
            return comparison != 0 ? comparison : left.VerificationKey.CompareTo(right.VerificationKey);
        });
        int unique = 0;
        for (int i = 0; i < result.Count; i++)
        {
            if (unique == 0 || !result[i].Equals(result[unique - 1])) result[unique++] = result[i];
        }
        result.RemoveRange(unique, result.Count - unique);
        return result;
    }

    /// <summary>
    /// EIP-8288 <c>discard_dependencies</c>: the dependencies covered by the input proofs that no retained transaction
    /// declares, in canonical order.
    /// </summary>
    /// <remarks>Wrapper pruning, FOCIL merging and block assembly all use this rule, so a dependency shared with any retained
    /// transaction is kept even when another transaction declaring it is removed.</remarks>
    /// <param name="inputDeps">The union of the dependencies the input proofs cover, including mode-0 direct witnesses.</param>
    /// <param name="retainedDeps">The dependency union of the selected transactions.</param>
    /// <exception cref="ArgumentException">A retained dependency is not covered by the input proofs.</exception>
    public static List<FrameDependency> DiscardDependencies(IEnumerable<FrameDependency> inputDeps, IEnumerable<FrameDependency> retainedDeps)
    {
        HashSet<FrameDependency> inputs = [.. inputDeps];
        HashSet<FrameDependency> retained = [.. retainedDeps];
        if (!retained.IsSubsetOf(inputs))
            throw new ArgumentException("Retained dependencies must be covered by the input proofs.", nameof(retainedDeps));
        inputs.ExceptWith(retained);
        return Canonicalize(inputs);
    }

    /// <summary>Counts dependencies per scheme (leanSPHINCS, any leanSTARK) for the mempool/tx limits.</summary>
    public static (int Sphincs, int Stark) CountByScheme(IReadOnlyList<FrameDependency> dependencies)
    {
        int sphincs = 0;
        int stark = 0;
        for (int i = 0; i < dependencies.Count; i++)
        {
            if (dependencies[i].Scheme == Eip8288Constants.LeanSphincsScheme) sphincs++;
            else stark++;
        }

        return (sphincs, stark);
    }

    public static ValueHash256 ComputeBlockDepsHash(Block block, Func<Transaction, ValueHash256> computeSignatureHash) =>
        ComputeDepsHash(ForBlock(block, computeSignatureHash));
}
