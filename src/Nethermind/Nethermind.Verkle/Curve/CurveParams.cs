// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using Nethermind.Int256;
using Nethermind.Verkle.Fields.FpEElement;

namespace Nethermind.Verkle.Curve;

public readonly struct CurveParams
{
    private static readonly byte[] _numY =
    [
        102, 65, 151, 204, 182, 103, 49, 94, 96, 100, 228, 238, 129, 173, 140, 53, 134, 213, 220, 186, 80, 139, 125,
        21, 15, 62, 18, 218, 158, 102, 108, 42
    ];

    private static readonly byte[] _numX =
    [
        24, 174, 82, 162, 102, 24, 231, 225, 101, 132, 153, 173, 34, 192, 121, 43, 243, 66, 190, 123, 119, 17, 55,
        116, 197, 52, 11, 44, 204, 50, 193, 41
    ];

    public static readonly FpE A = FpE.SetElement(5).Negative();

    public static readonly FpE YTe = FpE.FromBytesReduced(_numY);
    public static readonly FpE XTe = FpE.FromBytesReduced(_numX);

    private static readonly Lazy<FpE> _d = new(() =>
    {
        UInt256.TryParse("45022363124591815672509500913686876175488063829319466900776701791074614335719",
            out UInt256 x);
        return FpE.SetElement(x.u0, x.u1, x.u2, x.u3);
    });

    public static readonly FpE D = _d.Value;

    // public static readonly FpE Cofactor = FpE.SetElement(4);
}
