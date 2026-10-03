// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>Public output-capacity accounting for the prototype's carried generic proofs.</summary>
public static class LeanProofCapacity
{
    /// <summary>Reads generic witness lengths from an already verified, canonical NLR2 envelope without copying blobs.</summary>
    public static bool TryReadGenericWitnessLengths(ReadOnlySpan<byte> proof, IDictionary<FrameDependency, int> lengths)
    {
        if (proof.Length > Eip8288Constants.MaxProofBytes || proof.Length < 16 || !proof[..4].SequenceEqual("NLR2"u8)) return false;
        int position = 4;
        if (!ReadCount(proof, ref position, Eip8288Constants.MaxProofDependencies, out int count)
            || count > (proof.Length - position) / Eip8288Constants.DependencyTripleLength) return false;
        Span<FrameDependency> generic = stackalloc FrameDependency[Eip8288Constants.MaxGenericStarkProofs];
        int genericCount = 0;
        bool sphincs = false;
        ReadOnlySpan<byte> previous = default;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> triple = proof.Slice(position, Eip8288Constants.DependencyTripleLength);
            position += Eip8288Constants.DependencyTripleLength;
            if (triple[..31].IndexOfAnyExcept((byte)0) >= 0 || !previous.IsEmpty && previous.SequenceCompareTo(triple) >= 0) return false;
            previous = triple;
            if (triple[31] == Eip8288Constants.LeanSphincsScheme) sphincs = true;
            else if (triple[31] == Eip8288Constants.LeanStarkScheme && genericCount < generic.Length)
                generic[genericCount++] = ParseDependency(triple);
            else return false;
        }
        if (!ReadCount(proof, ref position, Eip8288Constants.MaxSphincsGuestProofBytes, out int sphincsBytes)
            || (sphincsBytes > 0) != sphincs || sphincsBytes > proof.Length - position) return false;
        position += sphincsBytes;
        if (!ReadCount(proof, ref position, Eip8288Constants.MaxGenericStarkProofs, out int carried) || carried != genericCount) return false;
        Span<int> sizes = stackalloc int[Eip8288Constants.MaxGenericStarkProofs];
        for (int i = 0; i < carried; i++)
        {
            if (proof.Length - position < Eip8288Constants.DependencyTripleLength) return false;
            ReadOnlySpan<byte> triple = proof.Slice(position, Eip8288Constants.DependencyTripleLength);
            position += Eip8288Constants.DependencyTripleLength;
            if (triple[..31].IndexOfAnyExcept((byte)0) >= 0 || !ParseDependency(triple).Equals(generic[i])
                || !ReadCount(proof, ref position, Eip8288Constants.MaxProofBytes, out int bytes)
                || bytes == 0 || bytes > proof.Length - position) return false;
            sizes[i] = bytes;
            position += bytes;
        }
        if (position != proof.Length) return false;
        for (int i = 0; i < carried; i++) AddWitnessLength(lengths, generic[i], sizes[i]);
        return true;
    }

    /// <summary>Retains the shortest verified witness when the same public claim has multiple encodings.</summary>
    public static void AddWitnessLength(IDictionary<FrameDependency, int> lengths, FrameDependency dependency, int bytes)
    {
        if (!lengths.TryGetValue(dependency, out int previous) || previous > bytes) lengths[dependency] = bytes;
    }

    /// <summary>Checks distinct required claims against the public count and reserved-output bounds.</summary>
    public static string? CapacityError(IReadOnlySet<FrameDependency> required, IReadOnlyDictionary<FrameDependency, int> lengths)
    {
        if (required.Count > Eip8288Constants.MaxProofDependencies) return "Dependency proof count limit exceeded";
        long bytes = 16 + (long)required.Count * Eip8288Constants.DependencyTripleLength;
        int generic = 0;
        bool sphincs = false;
        foreach (FrameDependency dependency in required)
        {
            if (dependency.Scheme == Eip8288Constants.LeanSphincsScheme) sphincs = true;
            else if (dependency.Scheme == Eip8288Constants.LeanStarkScheme)
            {
                if (++generic > Eip8288Constants.MaxGenericStarkProofs) return "Generic STARK proof count limit exceeded";
                if (!lengths.TryGetValue(dependency, out int length) || length <= 0) return "Missing generic STARK witness length";
                bytes += Eip8288Constants.DependencyTripleLength + 4L + length;
            }
            else return "Unknown dependency proof scheme";
        }
        if (sphincs) bytes += Eip8288Constants.MaxSphincsGuestProofBytes;
        return bytes > Eip8288Constants.MaxProofBytes ? "Dependency proof output limit exceeded" : null;
    }

    private static FrameDependency ParseDependency(ReadOnlySpan<byte> triple)
        => new(triple[31], new ValueHash256(triple[32..64]), new ValueHash256(triple[64..96]));

    private static bool ReadCount(ReadOnlySpan<byte> proof, ref int position, int maximum, out int count)
    {
        count = 0;
        if (proof.Length - position < 4) return false;
        count = BinaryPrimitives.ReadInt32LittleEndian(proof[position..]);
        position += 4;
        return count >= 0 && count <= maximum;
    }
}
