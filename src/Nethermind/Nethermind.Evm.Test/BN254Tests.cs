// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <remarks>
/// The precompile forwarders (<see cref="BN254AddPrecompile"/>, <see cref="BN254MulPrecompile"/>) always
/// normalise call data to the exact length before invoking <see cref="BN254"/>, so these length guards are
/// unreachable through the public surface. They are exercised directly here because a violated length would
/// otherwise read or write past the backing buffer through the raw pointers inside <c>BN254</c>.
/// </remarks>
public class BN254Tests
{
    // 128-byte input / 64-byte output vector from BN254AddPrecompileTests.
    private const string ValidAddInput =
        "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b3625f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd5850b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cf";

    // 96-byte input / 64-byte output vector from BN254MulPrecompileTests.
    private const string ValidMulInput =
        "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    [Test]
    public void Add_rejects_wrong_input_length([Values(0, 64, 96, 127, 129, 192)] int inputLength) =>
        Assert.That(BN254.Add(new byte[64], new byte[inputLength]), Is.False);

    [Test]
    public void Add_rejects_short_output([Values(0, 63)] int outputLength) =>
        Assert.That(BN254.Add(new byte[outputLength], new byte[128]), Is.False);

    [Test]
    public void Add_accepts_exact_or_oversized_output([Values(64, 128)] int outputLength) =>
        Assert.That(BN254.Add(new byte[outputLength], Bytes.FromHexString(ValidAddInput)), Is.True);

    [Test]
    public void Mul_rejects_wrong_input_length([Values(0, 64, 95, 97, 128)] int inputLength) =>
        Assert.That(BN254.Mul(new byte[64], new byte[inputLength]), Is.False);

    [Test]
    public void Mul_rejects_short_output([Values(0, 63)] int outputLength) =>
        Assert.That(BN254.Mul(new byte[outputLength], new byte[96]), Is.False);

    [Test]
    public void Mul_accepts_exact_or_oversized_output([Values(64, 128)] int outputLength) =>
        Assert.That(BN254.Mul(new byte[outputLength], Bytes.FromHexString(ValidMulInput)), Is.True);
}
