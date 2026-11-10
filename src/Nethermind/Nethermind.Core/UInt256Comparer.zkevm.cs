// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

public sealed partial class UInt256Comparer
{
    public static partial IEqualityComparer<UInt256>? GetOptimized() => Instance;

    public partial int GetHashCode(UInt256 obj)
    {
        // Passed as words: taking the parameter's address would copy the caller's index into a temporary first.
        ulong hash = SpanExtensions.MixSlotIndex(obj.u0, obj.u1, obj.u2, obj.u3);
        return (int)(hash ^ (hash >> 32));
    }
}
