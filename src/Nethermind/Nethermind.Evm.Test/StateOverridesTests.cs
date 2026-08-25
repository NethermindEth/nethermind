// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.State.OverridableEnv;
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

    private static IEnumerable<TestCaseData> ValidNonceCases() =>
    [
        new TestCaseData(ulong.MaxValue).SetName("ulong_max"),
        new TestCaseData(ulong.MaxValue - 1).SetName("ulong_max_minus_one"),
        new TestCaseData(0ul).SetName("zero"),
    ];

    [TestCaseSource(nameof(ValidNonceCases))]
    public void nonce_override_within_uint64_range_does_not_throw(ulong nonce)
    {
        Dictionary<Address, AccountOverride> overrides = new()
        {
            { TestItem.AddressA, new AccountOverride { Nonce = nonce } }
        };

        Action act = () => _state.ApplyStateOverridesNoCommit(_codeRepo, overrides, Shanghai.Instance);

        Assert.That(act, Throws.Nothing);
    }

    [Test]
    public void override_with_no_state_fields_does_not_create_account()
    {
        // An override with no state-changing fields (e.g. movePrecompileToAddress only)
        // must not inject an empty account into the trie — that would alter the stateRoot.
        Dictionary<Address, AccountOverride> overrides = new()
        {
            { TestItem.AddressA, new AccountOverride() },
        };

        _state.ApplyStateOverridesNoCommit(_codeRepo, overrides, Shanghai.Instance);

        Assert.That(_state.TryGetAccount(TestItem.AddressA, out _), Is.False);
    }

    [Test]
    public void code_override_stamps_code_hash_and_shares_code_info_across_requests()
    {
        byte[] code = [0x60, 0x01, 0x5b, 0x00];
        StaticCodeCache codeCache = new(16);
        OverridableCodeInfoRepository firstRequest = new(Substitute.For<ICodeInfoRepository>(), _state, codeCache);
        OverridableCodeInfoRepository secondRequest = new(Substitute.For<ICodeInfoRepository>(), _state, codeCache);
        Dictionary<Address, AccountOverride> overrides = new()
        {
            { TestItem.AddressA, new AccountOverride { Code = code } },
        };

        _state.ApplyStateOverridesNoCommit(firstRequest, overrides, Shanghai.Instance);
        _state.ApplyStateOverridesNoCommit(secondRequest, overrides, Shanghai.Instance);

        CodeInfo codeInfo = firstRequest.GetCachedCodeInfo(TestItem.AddressA, Shanghai.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeInfo.CodeHash, Is.EqualTo(ValueKeccak.Compute(code)));
            Assert.That(secondRequest.GetCachedCodeInfo(TestItem.AddressA, Shanghai.Instance), Is.SameAs(codeInfo));
        }
    }

    [Test]
    public void override_with_balance_creates_account_in_state()
    {
        Dictionary<Address, AccountOverride> overrides = new()
        {
            { TestItem.AddressA, new AccountOverride { Balance = 100 } },
        };

        _state.ApplyStateOverridesNoCommit(_codeRepo, overrides, Shanghai.Instance);

        Assert.That(_state.TryGetAccount(TestItem.AddressA, out _), Is.True);
    }
}
