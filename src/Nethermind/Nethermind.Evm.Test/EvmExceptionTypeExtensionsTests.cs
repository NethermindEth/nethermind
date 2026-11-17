// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EvmExceptionTypeExtensionsTests
{
    [Test]
    public void FastParse_inverts_FastToString([Values] EvmExceptionType type)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(type.FastToString(), Is.EqualTo(type.ToString()));
            Assert.That(EvmExceptionTypeExtensions.FastParse(type.FastToString()), Is.EqualTo(type));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("outOfGas")]
    [TestCase("5")]
    public void FastParse_returns_None_for_unknown_name(string? name) =>
        Assert.That(EvmExceptionTypeExtensions.FastParse(name), Is.EqualTo(EvmExceptionType.None));
}
