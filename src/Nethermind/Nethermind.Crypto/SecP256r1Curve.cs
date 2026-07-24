// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Crypto;

public static class SecP256r1Curve
{
    /// <summary>The secp256r1 (NIST P-256) group order <c>N</c>.</summary>
    public static readonly UInt256 N = new(0xf3b9cac2fc632551ul, 0xbce6faada7179e84ul, 0xfffffffffffffffful, 0xffffffff00000000ul);

    /// <summary>Half the group order, i.e. <c>N / 2</c>; the low-s canonicality bound.</summary>
    public static readonly UInt256 HalfN = N / 2;
}
