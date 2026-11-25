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
/// The guest's MODEXP of an empty base or exponent, or of a base equal to the modulus, which it answers without the
/// accelerator (EIP-198: 0^0 = 1).
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
    [TestCase("07", "", "0101", ExpectedResult = "0001", TestName = "an empty exponent gives 1 modulo a modulus ending in 01")]
    [TestCase("07", "", "05", ExpectedResult = "01", TestName = "an empty exponent gives 1 modulo a one-byte modulus")]
    [TestCase(Modulus, Exponent, Modulus, ExpectedResult = Zero, TestName = "a base equal to the modulus is 0")]
    [TestCase("00" + Modulus, Exponent, "0000" + Modulus, ExpectedResult = "0000" + Zero, TestName = "a base equal to the modulus but for leading zeros is 0")]
    [TestCase(Modulus, Zero, Modulus, ExpectedResult = One, TestName = "a base equal to the modulus to a zero exponent is 1")]
    public string Empty_operands_yield_the_eip198_result(string @base, string exponent, string modulus)
    {
        byte[] input = Bytes.Concat(
            Length(@base), Length(exponent), Length(modulus),
            Bytes.FromHexString(@base), Bytes.FromHexString(exponent), Bytes.FromHexString(modulus));

        Result<byte[]> result = ModExpPrecompile.Instance.Run(input, Osaka.Instance);

        Assert.That((bool)result, Is.True);
        return result.Data!.ToHexString();
    }

    [TestCase("ff00ff", "ff00ff", ExpectedResult = true, TestName = "a base equal to the modulus")]
    [TestCase("0000ff00ff", "ff00ff", ExpectedResult = true, TestName = "a base equal to the modulus but for leading zeros")]
    [TestCase("ff01ff", "ff00ff", ExpectedResult = false, TestName = "a base that differs from the modulus inside")]
    [TestCase("0001ff00ff", "ff00ff", ExpectedResult = false, TestName = "a base that differs from the modulus inside its padding")]
    [TestCase("ff00", "ff00ff", ExpectedResult = false, TestName = "a base shorter than the modulus")]
    public bool Base_equals_the_modulus_only_when_every_byte_matches(string @base, string modulus) =>
        ModExpPrecompile.IsModulus(Bytes.FromHexString(@base), Bytes.FromHexString(modulus));

    private static byte[] Length(string hex) => ((UInt256)(ulong)(hex.Length / 2)).ToBigEndian();
}
