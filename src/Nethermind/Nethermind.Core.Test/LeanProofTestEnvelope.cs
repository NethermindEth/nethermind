// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Nethermind.Core.Test;

/// <summary>Canonical envelope-shape fixtures for tests with an explicitly substituted crypto verifier.</summary>
public static class LeanProofTestEnvelope
{
    public static byte[] Create(IReadOnlyList<FrameDependency> dependencies)
        => Create(dependencies, new Dictionary<FrameDependency, int>());

    public static byte[] Create(IReadOnlyList<FrameDependency> dependencies, IReadOnlyDictionary<FrameDependency, int> lengths)
    {
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(dependencies);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("NLR2"u8);
        writer.Write(canonical.Count);
        Span<byte> triple = stackalloc byte[Eip8288Constants.DependencyTripleLength];
        int generic = 0;
        bool sphincs = false;
        foreach (FrameDependency dependency in canonical)
        {
            dependency.WriteTo(triple);
            writer.Write(triple);
            if (dependency.Scheme == Eip8288Constants.LeanStarkScheme) generic++;
            else sphincs = true;
        }
        writer.Write(sphincs ? 1 : 0);
        if (sphincs) writer.Write((byte)1);
        writer.Write(generic);
        foreach (FrameDependency dependency in canonical)
        {
            if (dependency.Scheme != Eip8288Constants.LeanStarkScheme) continue;
            dependency.WriteTo(triple);
            writer.Write(triple);
            int length = lengths.TryGetValue(dependency, out int bytes) ? bytes : 1;
            writer.Write(length);
            writer.Write(new byte[length]);
        }
        return stream.ToArray();
    }
}
