// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class StateOverridesTests
{
    private IWorldState _state = null!;
    private IOverridableCodeInfoRepository _codeRepo = null!;
    private IDisposable _stateScope = null!;

    [SetUp]
    public void SetUp()
    {
        _state = TestWorldStateFactory.CreateForTest();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _codeRepo = Substitute.For<IOverridableCodeInfoRepository>();
    }

    [TearDown]
    public void TearDown() => _stateScope.Dispose();

    // Only values that don't fit in a ulong are rejected (> ulong.MaxValue).
    // ulong.MaxValue itself is valid — consistent with Geth's hexutil.Uint64 type.
    private static IEnumerable<TestCaseData> InvalidNonceCases() =>
    [
        new TestCaseData((UInt256)ulong.MaxValue + 1).SetName("ulong_max_plus_one"),
        new TestCaseData(UInt256.MaxValue).SetName("uint256_max"),
    ];

    [TestCaseSource(nameof(InvalidNonceCases))]
    public void nonce_override_above_uint64_range_throws(UInt256 nonce)
    {
        Dictionary<Address, AccountOverride> overrides = new()
        {
            { TestItem.AddressA, new AccountOverride { Nonce = nonce } }
        };

        Action act = () => _state.ApplyStateOverridesNoCommit(_codeRepo, overrides, Shanghai.Instance);

        act.Should().Throw<ArgumentException>().WithMessage("*maximum supported value*");
    }

    private static IEnumerable<TestCaseData> ValidNonceCases() =>
    [
        new TestCaseData((UInt256)ulong.MaxValue).SetName("ulong_max"),
        new TestCaseData((UInt256)ulong.MaxValue - 1).SetName("ulong_max_minus_one"),
        new TestCaseData(UInt256.Zero).SetName("zero"),
    ];

    [TestCaseSource(nameof(ValidNonceCases))]
    public void nonce_override_within_uint64_range_does_not_throw(UInt256 nonce)
    {
        Dictionary<Address, AccountOverride> overrides = new()
        {
            { TestItem.AddressA, new AccountOverride { Nonce = nonce } }
        };

        Action act = () => _state.ApplyStateOverridesNoCommit(_codeRepo, overrides, Shanghai.Instance);

        act.Should().NotThrow();
    }
}
