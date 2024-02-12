// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Verkle.Tree.Utils;

public class ByteListComparer : Comparer<byte[]>
{
    public override int Compare(byte[]? x, byte[]? y)
    {
        if (x is null) return y is null ? 0 : -1;

        return y is null ? 1 : Bytes.BytesComparer.CompareDiffLength(x, y);
    }
}

public class ListEqualityComparer : EqualityComparer<byte[]>
{
    public override bool Equals(byte[]? x, byte[]? y)
    {
        return Bytes.SpanEqualityComparer.Equals(x, y);
    }

    public override int GetHashCode(byte[] obj)
    {
        return obj.GetSimplifiedHashCode();
    }
}

public class ListWithByteComparer : Comparer<(byte[], byte)>
{
    public override int Compare((byte[], byte) x, (byte[], byte) y)
    {
        var comp = new ByteListComparer();
        var val = comp.Compare(x.Item1, y.Item1);
        return val == 0 ? x.Item2.CompareTo(y.Item2) : val;
    }
}

public class ListWithByteEqualityComparer : EqualityComparer<(byte[], byte)>
{
    public override bool Equals((byte[], byte) x, (byte[], byte) y)
    {
        var isBytesSame =
            Bytes.SpanEqualityComparer.Equals(x.Item1, y.Item1);
        if (isBytesSame) return x.Item2 == y.Item2;
        return isBytesSame;
    }

    public override int GetHashCode((byte[], byte) obj)
    {
        return HashCode.Combine(obj.Item1.GetSimplifiedHashCode(),
            obj.Item2.GetHashCode());
    }
}
