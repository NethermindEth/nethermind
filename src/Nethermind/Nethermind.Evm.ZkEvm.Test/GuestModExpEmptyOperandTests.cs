// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>
/// The guest's MODEXP of an empty base or exponent, which it answers without the accelerator (EIP-198: 0^0 = 1).
/// </summary>
public class GuestModExpEmptyOperandTests
{
    private const string Exponent = "fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2e";
    private const string Modulus = "fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f";
    private const string One = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string Zero = "0000000000000000000000000000000000000000000000000000000000000000";

    [TestCase("", Exponent, Modulus, ExpectedResult = Zero, TestName = "an empty base is 0 for a nonzero exponent")]
    [TestCase("", Zero, Modulus, ExpectedResult = One, TestName = "an empty base to a zero exponent is 1")]
    [TestCase("", "", Modulus, ExpectedResult = One, TestName = "an empty base to an empty exponent is 1")]
    [TestCase(Modulus, "", Modulus, ExpectedResult = One, TestName = "an empty exponent gives 1")]
    [TestCase("07", "", "01", ExpectedResult = "00", TestName = "an empty exponent gives 0 modulo 1")]
    [TestCase("07", "", "0001", ExpectedResult = "0000", TestName = "an empty exponent gives 0 modulo a padded 1")]
    [TestCase("", "05", "0100", ExpectedResult = "0000", TestName = "an empty base gives 0 modulo a multi-byte modulus")]
    public string Empty_operands_yield_the_eip198_result(string @base, string exponent, string modulus)
    {
        byte[] input = Bytes.Concat(
            Length(@base), Length(exponent), Length(modulus),
            Bytes.FromHexString(@base), Bytes.FromHexString(exponent), Bytes.FromHexString(modulus));

        Result<byte[]> result = ModExpPrecompile.Instance.Run(input, Osaka.Instance);

        Assert.That((bool)result, Is.True);
        return result.Data!.ToHexString();
    }

    private static byte[] Length(string hex) => ((UInt256)(ulong)(hex.Length / 2)).ToBigEndian();
}
