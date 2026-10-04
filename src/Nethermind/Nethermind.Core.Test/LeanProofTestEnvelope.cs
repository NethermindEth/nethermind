// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Nethermind.Core.Test;

/// <summary>Opaque claim fixtures for tests with an explicitly substituted crypto verifier.</summary>
public static class LeanProofTestEnvelope
{
    public static byte[] Create(IReadOnlyList<FrameDependency> dependencies)
        => Create(dependencies, 1);

    public static byte[] Create(IReadOnlyList<FrameDependency> dependencies, int paddingBytes)
    {
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(dependencies);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("NLTF"u8);
        writer.Write(canonical.Count);
        Span<byte> triple = stackalloc byte[Eip8288Constants.DependencyTripleLength];
        foreach (FrameDependency dependency in canonical)
        {
            dependency.WriteTo(triple);
            writer.Write(triple);
        }
        writer.Write(new byte[paddingBytes]);
        return stream.ToArray();
    }
}
