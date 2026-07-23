// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// A single EIP-8288 dependency triple <c>(scheme, data_hash, verification_key)</c> declared by a
/// dependency-verification frame. <c>data_hash</c>/<c>verification_key</c> are the message hash and
/// public key (leanSPHINCS) or the public-inputs hash and STARK verification key (leanSTARK).
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
        destination[..Eip8288Constants.DependencyTripleLength].Clear();
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
    public static bool IsDependencyFrame(TxFrame frame) => frame.Mode == Eip8288Constants.DepVerifyFrameMode;

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

    /// <summary>Spec <c>dependencies(tx)</c>: all triples from all dependency-verification frames.</summary>
    public static IEnumerable<FrameDependency> ForTransaction(Transaction tx)
    {
        TxFrame[]? frames = tx.Frames;
        if (frames is null) yield break;

        foreach (TxFrame frame in frames)
        {
            if (!IsDependencyFrame(frame)) continue;
            foreach (FrameDependency dep in ParseFrame(frame)) yield return dep;
        }
    }

    /// <summary>Spec <c>dependencies(block)</c>: all transaction dependencies in inclusion order.</summary>
    public static List<FrameDependency> ForBlock(Block block)
    {
        List<FrameDependency> dependencies = [];
        foreach (Transaction tx in block.Transactions)
        {
            foreach (FrameDependency dep in ForTransaction(tx)) dependencies.Add(dep);
        }

        return dependencies;
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

    /// <summary>
    /// Spec <c>block_deps_hash</c>: <c>hash(concat(bytes32_be(scheme) || data_hash || verification_key))</c>.
    /// EIP8288-ISSUE: the hash function is unspecified (candidates Poseidon / BLAKE3); Keccak-256 is
    /// used as the placeholder.
    /// </summary>
    public static ValueHash256 ComputeDepsHash(IReadOnlyList<FrameDependency> dependencies) =>
        ValueKeccak.Compute(Serialize(dependencies));

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

    public static ValueHash256 ComputeBlockDepsHash(Block block) => ComputeDepsHash(ForBlock(block));
}
