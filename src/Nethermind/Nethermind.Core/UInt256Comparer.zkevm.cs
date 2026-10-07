// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

public sealed partial class UInt256Comparer
{
    public static partial IEqualityComparer<UInt256>? GetOptimized() => Instance;

    public partial int GetHashCode(UInt256 obj)
    {
        ulong hash = SpanExtensions.MixSlotIndex(ref Unsafe.As<UInt256, byte>(ref obj));
        return (int)(hash ^ (hash >> 32));
    }
}
