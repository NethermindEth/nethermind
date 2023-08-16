// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Verkle.Tree.Proofs;

public class ListComparer : Comparer<List<byte>>
{
    public override int Compare(List<byte>? x, List<byte>? y)
    {
        if (x is null)
        {
            return y is null ? 0 : -1;
        }

        return y is null ? 1 : Bytes.BytesComparer.CompareDiffLength(x.ToArray(), y.ToArray());
    }
}

public class ListEqualityComparer : EqualityComparer<List<byte>>
{
    public override bool Equals(List<byte>? x, List<byte>? y)
    {
        return Bytes.SpanEqualityComparer.Equals(CollectionsMarshal.AsSpan(x), CollectionsMarshal.AsSpan(y));
    }

    public override int GetHashCode(List<byte> obj)
    {
        return Bytes.GetSimplifiedHashCode(CollectionsMarshal.AsSpan(obj));
    }
}

public class ListWithByteComparer : Comparer<(List<byte>, byte)>
{
    public override int Compare((List<byte>, byte) x, (List<byte>, byte) y)
    {
        ListComparer comp = new ListComparer();
        int val = comp.Compare(x.Item1, y.Item1);
        return val == 0 ? x.Item2.CompareTo(y.Item2) : val;
    }
}

public class ListWithByteEqualityComparer : EqualityComparer<(List<byte>, byte)>
{
    public override bool Equals((List<byte>, byte) x, (List<byte>, byte) y)
    {
        bool isBytesSame = Bytes.SpanEqualityComparer.Equals(CollectionsMarshal.AsSpan(x.Item1), CollectionsMarshal.AsSpan(y.Item1));
        if (isBytesSame)
        {
            return x.Item2 == y.Item2;
        }
        return isBytesSame;
    }

    public override int GetHashCode((List<byte>, byte) obj)
    {
        return HashCode.Combine(Bytes.GetSimplifiedHashCode(CollectionsMarshal.AsSpan(obj.Item1)), obj.Item2.GetHashCode());
    }
}
