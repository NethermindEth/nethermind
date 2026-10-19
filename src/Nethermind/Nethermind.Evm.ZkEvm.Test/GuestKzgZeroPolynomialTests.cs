// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>
/// The guest's point evaluation of a commitment and proof both at infinity, which it decides without the
/// accelerator, against what c-kzg answers for the same inputs in <c>KzgPointEvaluationPrecompileTests</c>.
/// </summary>
public class GuestKzgZeroPolynomialTests
{
    private const string Infinity = "c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
    private const string Zero = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Modulus = "73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001";

    [TestCase(Zero, Zero, ExpectedResult = true)]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000001", Zero, ExpectedResult = true)]
    [TestCase("5eb7004fe57383e6c88b99d839937fddf3f99279353aaf8d5c9a75f91ce33c62", Zero, ExpectedResult = true)]
    [TestCase("73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000000", Zero, ExpectedResult = true)]
    [TestCase("5eb7004fe57383e6c88b99d839937fddf3f99279353aaf8d5c9a75f91ce33c62", "0000000000000000000000000000000000000000000000000000000000000001", ExpectedResult = false)]
    [TestCase(Zero, "73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000000", ExpectedResult = false)]
    [TestCase(Zero, Modulus, ExpectedResult = false)]
    [TestCase(Modulus, Zero, ExpectedResult = false)]
    [TestCase("ff00000000000000000000000000000000000000000000000000000000000000", Zero, ExpectedResult = false)]
    public bool Zero_polynomial_evaluates_to_zero_at_any_canonical_point(string z, string y) =>
        KzgPolynomialCommitments.VerifyProof(Bytes.FromHexString(Infinity), Bytes.FromHexString(z), Bytes.FromHexString(y), Bytes.FromHexString(Infinity));
}
