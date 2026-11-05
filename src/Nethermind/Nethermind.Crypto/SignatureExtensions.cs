// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Crypto;

public static class SignatureExtensions
{
    /// <summary>
    /// Whether the signature's <c>s</c> value is canonical low-S (<c>s &lt;= n/2</c>),
    /// rejecting (r, s) → (r, n−s) malleability.
    /// </summary>
    public static bool HasLowS(this Signature signature)
    {
        UInt256 s = new(signature.SAsSpan, true);
        return s <= SecP256k1Curve.HalfN;
    }
}
