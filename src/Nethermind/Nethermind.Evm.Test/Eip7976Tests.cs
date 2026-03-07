// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-7976: Increase Calldata Floor Cost.
/// Verifies both standard and floor gas for various calldata patterns.
/// </summary>
[TestFixture]
public class Eip7976Tests
{
    private static OverridableReleaseSpec Eip7976Spec => new OverridableReleaseSpec(Prague.Instance)
    {
        IsEip7976Enabled = true
    };

    // Roughly
    // Standard: 21000 + zero*4 + nonzero*16
    // Floor: 21000 + byteCount * 4 * 16
    [TestCase(new byte[] { 1 }, 21_016, 21_064, TestName = "Single nonzero byte")]
    [TestCase(new byte[] { 0 }, 21_004, 21_064, TestName = "Single zero byte")]
    [TestCase(new byte[] { 0, 0, 1, 1 }, 21_040, 21_256, TestName = "Multi byte mixed data")]
    [TestCase(new byte[0], 21_000, 21_000, TestName = "Empty data is just transaction base")]
    public void Intrinsic_gas_with_eip7976(byte[] data, long expectedStandard, long expectedFloor)
    {
        Transaction transaction = new() { Data = data, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Eip7976Spec);
        cost.Should().Be(new EthereumIntrinsicGas(expectedStandard, expectedFloor));
    }

    [Test]
    public void Contract_creation_standard_exceeds_floor()
    {
        Transaction transaction = new() { Data = new byte[] { 1 }, To = null };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Eip7976Spec);
        // Standard: 21000 (base) + 32000 (tx creation) + InitCodeWord * 1 + 1 * 16 (1 non-zero byte) = 53,018
        // Floor Gas Cost: 21000 (base) + 1 * 4 * 16 (1 non-zero byte) = 21,064
        cost.Should().Be(new EthereumIntrinsicGas(Standard: 53_018, FloorGas: 21_064));
    }

    [Test]
    public void Disabled_eip7976_falls_back_to_eip7623()
    {
        // 1 zero byte -> tokens = 1, floor = 21000 + 1 * 10 = 21010
        Transaction transaction = new() { Data = new byte[] { 0 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(21_004, 21_010));
    }
}
