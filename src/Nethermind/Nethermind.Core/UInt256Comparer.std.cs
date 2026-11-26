// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

public sealed partial class UInt256Comparer
{
    public static partial IEqualityComparer<UInt256>? GetOptimized() => null;

    public partial int GetHashCode(UInt256 obj) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
}
