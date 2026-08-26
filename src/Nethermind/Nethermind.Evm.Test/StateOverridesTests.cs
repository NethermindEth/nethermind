// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Constraints;

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

    /// <remarks>Override code above the spec's code-size limit is served but kept out of the cache, whose footprint it would otherwise unbound.</remarks>
    [TestCase(false)]
    [TestCase(true)]
    public void code_override_stamps_code_hash_and_shares_code_info_across_requests_within_code_size_limit(bool oversized)
    {
        byte[] code = new byte[oversized ? Shanghai.Instance.MaxCodeSize + 1 : 4];
        code.AsSpan(0, 4).Fill(0x5b);
        StaticCodeCache codeCache = new(16);
        OverridableCodeInfoRepository firstRequest = new(Substitute.For<ICodeInfoRepository>(), _state, codeCache);
        OverridableCodeInfoRepository secondRequest = new(Substitute.For<ICodeInfoRepository>(), _state, codeCache);
        Dictionary<Address, AccountOverride> overrides = new()
        {
            { TestItem.AddressA, new AccountOverride { Code = code } },
        };

        _state.ApplyStateOverridesNoCommit(firstRequest, overrides, Shanghai.Instance);
        _state.ApplyStateOverridesNoCommit(secondRequest, overrides, Shanghai.Instance);

        ValueHash256 codeHash = ValueKeccak.Compute(code);
        CodeInfo codeInfo = firstRequest.GetCachedCodeInfo(TestItem.AddressA, Shanghai.Instance);
        IResolveConstraint sharedAcrossRequests = oversized ? Is.Not.SameAs(codeInfo) : Is.SameAs(codeInfo);
        IResolveConstraint cached = oversized ? Is.Null : Is.SameAs(codeInfo);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeInfo.CodeHash, Is.EqualTo(codeHash));
            Assert.That(codeInfo.Code.ToArray(), Is.EqualTo(code));
            Assert.That(secondRequest.GetCachedCodeInfo(TestItem.AddressA, Shanghai.Instance), sharedAcrossRequests);
            Assert.That(codeCache.Get(in codeHash), cached);
        }
    }

    [Test]
    public void storage_override_is_written_eagerly_when_the_state_cannot_serve_it_lazily()
    {
        NonSinkWorldState state = new(_state);
        Dictionary<Address, AccountOverride> overrides = new()
        {
            { TestItem.AddressA, new AccountOverride { StateDiff = new Dictionary<UInt256, Hash256> { [1] = new Hash256(((UInt256)7).ToValueHash()) } } },
        };

        state.ApplyStateOverridesNoCommit(_codeRepo, overrides, Shanghai.Instance, lazyStorage: true);

        Assert.That(_state.Get(new StorageCell(TestItem.AddressA, 1)).ToArray(), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public void block_access_list_world_state_refuses_lazy_storage_overrides()
    {
        BlockAccessListBasedWorldState state = new(_state, LimboLogs.Instance);

        Assert.That(state.TrySetStorageOverrides(TestItem.AddressA, [], replaceAll: false), Is.False);
    }

    private sealed class NonSinkWorldState(IWorldState state) : WorldStateDecorator(state)
    {
        public override bool TrySetStorageOverrides(Address address, Dictionary<UInt256, Hash256> slots, bool replaceAll) => false;
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
