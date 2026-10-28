// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// A single EIP-8288 dependency triple <c>(scheme, data_hash, verification_key)</c> declared by a
/// dependency-verification frame. <c>data_hash</c>/<c>verification_key</c> are the message hash and
/// 32-byte public key (leanSPHINCS); leanSTARK is reserved.
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
    public static ulong RecursiveStarkGas(Transaction tx)
    {
        TxFrame[]? frames = tx.Frames;
        if (frames is null) return 0;

        ulong count = 0;
        foreach (TxFrame frame in frames)
        {
            if (IsDependencyFrame(frame)) count += (ulong)(frame.Data.Length / Eip8288Constants.DependencyTripleLength);
        }

        return count * Eip8288Constants.LeanStarkVerificationGas;
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

    /// <summary>Spec <c>get_deps_hash</c>, the <c>block_deps_hash</c> and <c>deps_hash</c> a recursive STARK is verified against:
    /// <c>DEPS_HASH</c> over the canonical dependencies' 64-byte <c>data_hash || key</c> entries, where the key is the raw leanSPHINCS
    /// public key a triple carries in its <c>verification_key_hash</c> field, not a hash of it.</summary>
    /// <remarks>EIP-8288 leaves <c>DEPS_HASH</c> TBD; this uses BLAKE2s-256, the hash the pinned aggregation circuit uses for its
    /// dependency digest. The digest omits the scheme, so it is defined only for leanSPHINCS lists (see
    /// <see cref="IsAcceptedScheme"/>).</remarks>
    public static ValueHash256 ComputeDepsHash(IReadOnlyList<FrameDependency> dependencies)
    {
        dependencies = Canonicalize(dependencies);
        Blake2s hash = Blake2s.Create();
        for (int i = 0; i < dependencies.Count; i++)
        {
            hash.Update(dependencies[i].DataHash.Bytes);
            hash.Update(dependencies[i].VerificationKey.Bytes);
        }

        return hash.Finish();
    }

    /// <summary>Whether a dependency scheme is enabled: leanSPHINCS only. EIP-8288 reserves leanSTARK, so a frame, wrapper,
    /// inclusion-list proof or aggregation input carrying it is invalid.</summary>
    public static bool IsAcceptedScheme(byte scheme) => scheme == Eip8288Constants.LeanSphincsScheme;

    /// <summary>Whether a dependency scheme is one EIP-8288 defines, enabled or reserved. Wrapper decoding accepts every known
    /// scheme, so a reserved one fails validation as a dependency-rule violation rather than as malformed encoding.</summary>
    public static bool IsKnownScheme(byte scheme) => scheme is Eip8288Constants.LeanSphincsScheme or Eip8288Constants.LeanStarkScheme;

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

    /// <summary>Counts dependencies per scheme (leanSPHINCS, leanSTARK) for the mempool/tx limits.</summary>
    public static (int Sphincs, int Stark) CountByScheme(IReadOnlyList<FrameDependency> dependencies)
    {
        int sphincs = 0;
        int stark = 0;
        for (int i = 0; i < dependencies.Count; i++)
        {
            if (dependencies[i].Scheme == Eip8288Constants.LeanStarkScheme) stark++;
            else sphincs++;
        }

        return (sphincs, stark);
    }

    public static ValueHash256 ComputeBlockDepsHash(Block block, Func<Transaction, ValueHash256> computeSignatureHash) =>
        ComputeDepsHash(ForBlock(block, computeSignatureHash));
}
