// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Crypto;

public static class SecP256r1Curve
{
    /// <summary>
    /// Group order for secp256r1 (NIST P-256) defined as <c>n</c> in
    /// <see href="https://www.secg.org/sec2-v2.pdf">Standards for Efficient Cryptography, SEC 2, 2.4.2</see>.
    /// </summary>
    // ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551
    public static readonly UInt256 N = new(0xf3b9cac2fc632551ul, 0xbce6faada7179e84ul, 0xfffffffffffffffful, 0xffffffff00000000ul);
}
