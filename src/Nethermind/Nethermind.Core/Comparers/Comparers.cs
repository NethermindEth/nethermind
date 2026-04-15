// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

using System.Collections.Generic;

namespace Nethermind.Core.Comparers;

// Concrete IComparer<int> for bflat AOT compatibility.
// Comparer<T>.Default uses runtime reflection that bflat cannot resolve.

public sealed class IntComparer : IComparer<int>
{
    public static IntComparer Instance { get; } = new();
    public int Compare(int x, int y) => x.CompareTo(y);
}

public sealed class UInt256Comparer : IComparer<UInt256>
{
    public static UInt256Comparer Instance { get; } = new();
    public int Compare(UInt256 x, UInt256 y) => x.CompareTo(y);
}
