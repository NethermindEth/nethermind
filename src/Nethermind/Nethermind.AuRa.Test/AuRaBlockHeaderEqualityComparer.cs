// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Test;
using NUnit.Framework.Constraints;

namespace Nethermind.AuRa.Test;

/// <summary>
/// Extends <see cref="TestEqualityComparers.BlockHeader"/> with the AuRa step + signature seal
/// check. Use when comparing AuRa-shaped headers; the base comparer ignores the seal fields.
/// </summary>
public sealed class AuRaBlockHeaderEqualityComparer(bool compareHash = true) : IEqualityComparer<BlockHeader>
{
    private readonly IEqualityComparer<BlockHeader> _baseComparer = TestEqualityComparers.BlockHeader(compareHash);

    public bool Equals(BlockHeader? actual, BlockHeader? expected)
    {
        if (!_baseComparer.Equals(actual, expected)) return false;
        if (actual is null || expected is null) return true;

        AuRaBlockHeader? actualSeal = actual as AuRaBlockHeader;
        AuRaBlockHeader? expectedSeal = expected as AuRaBlockHeader;
        if ((actualSeal is null) != (expectedSeal is null)) return false;
        return actualSeal is null
            || (actualSeal.AuRaStep == expectedSeal!.AuRaStep && BytesEqual(actualSeal.AuRaSignature, expectedSeal.AuRaSignature));
    }

    public int GetHashCode(BlockHeader obj) => 0;

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return System.MemoryExtensions.SequenceEqual<byte>(a, b);
    }
}

public static class AuRaTestEqualityConstraintExtensions
{
    public static EqualConstraint UsingAuRaBlockHeaderComparer(this EqualConstraint constraint, bool compareHash = true) =>
        constraint.Using(new AuRaBlockHeaderEqualityComparer(compareHash));
}
