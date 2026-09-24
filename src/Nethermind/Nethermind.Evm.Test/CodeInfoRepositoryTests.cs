// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain;
using Nethermind.Evm.State;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using System;
using Nethermind.State;

namespace Nethermind.Evm.Test;

[TestFixture, Parallelizable]
public class CodeInfoRepositoryTests
{
    private static readonly IReleaseSpec _releaseSpec;

    static CodeInfoRepositoryTests()
    {
        _releaseSpec = ReleaseSpecSubstitute.Create();
        _releaseSpec.Precompiles.Returns(FrozenSet<AddressAsKey>.Empty);
    }

    /// <summary>Precompile numbers a chain might register, including ones the index array cannot hold.</summary>
    /// <remarks>Ethereum's stop at 0x100 (RIP-7212), which is what the index array is sized against, but a
    /// plugin may register anywhere: Taiko's L1Sload and L1StaticCall sit at 0x10001 and 0x10002, and at
    /// 0x8000_0000 the number no longer fits an <see cref="int"/>, so it comes back negative. Indexing by
    /// precompile number is only sound if all of those are still resolved. The membership half of the same
    /// contract is covered against the real spec in <c>ReleaseSpecTests</c>; the substitute here answers
    /// <c>IsPrecompile</c> from its own arrangement, so it could not tell us anything about it.</remarks>
    private static readonly long[] PrecompileNumbers = [1, 2, 9, 0x11, 0x100, 0x101, 0x10001, 0x10002, 0x8000_0000];
    private const int MemoCapacityForTests = 4;

    [TestCaseSource(nameof(PrecompileNumbers))]
    public void Precompile_is_resolved_whatever_its_number(long number)
    {
        Address address = Address.FromNumber((UInt256)(ulong)number);
        CodeInfo expected = new(Substitute.For<IPrecompile>());

        IPrecompileProvider provider = Substitute.For<IPrecompileProvider>();
        provider.GetPrecompiles().Returns(new Dictionary<AddressAsKey, CodeInfo>
        {
            [Address.FromNumber(UInt256.One)] = new(Substitute.For<IPrecompile>()),
            [address] = expected,
        }.ToFrozenDictionary());

        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.Precompiles.Returns(new AddressAsKey[] { Address.FromNumber(UInt256.One), address }.ToFrozenSet());

        CodeInfoRepository repository = new(Substitute.For<IWorldState>(), provider);

        Assert.That(repository.GetCachedCodeInfo(address, false, spec, out _), Is.SameAs(expected));
    }

    private static IPrecompileProvider NoPrecompiles()
    {
        IPrecompileProvider provider = Substitute.For<IPrecompileProvider>();
        provider.GetPrecompiles().Returns(FrozenDictionary<AddressAsKey, CodeInfo>.Empty);
        return provider;
    }

    private sealed class CountingCodeCache : ICodeCache
    {
        private readonly StaticCodeCache _inner = new(64);

        public int GetCount { get; private set; }

        public CodeInfo? Get(in ValueHash256 codeHash)
        {
            GetCount++;
            return _inner.Get(in codeHash);
        }

        public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
            => _inner.Set(in codeHash, codeInfo);

        public void Clear() => _inner.Clear();
    }

    private sealed class CountingWorldState(IWorldState state) : WorldStateDecorator(state)
    {
        public int CodeReads { get; private set; }
        public int BytecodeAccesses { get; private set; }

        public override byte[]? GetCode(in ValueHash256 codeHash)
        {
            CodeReads++;
            return base.GetCode(in codeHash);
        }

        public override void RecordBytecodeAccess(Address address)
        {
            BytecodeAccesses++;
            base.RecordBytecodeAccess(address);
        }
    }

    /// <summary>Replacing or restoring an account's code must select the entry matching its current hash.</summary>
    /// <remarks>
    /// <see cref="CacheCodeInfoRepository"/> validates each memo entry against the hash re-read from world
    /// state on every call, so replacement and restore cannot answer with the wrong bytecode.
    /// </remarks>
    [Test]
    public void Changed_code_is_not_answered_from_the_last_resolved_code()
    {
        byte[] first = [(byte)Instruction.STOP];
        byte[] second = [(byte)Instruction.JUMPDEST, (byte)Instruction.STOP];

        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        CacheCodeInfoRepository repository = new(stateProvider, NoPrecompiles(), new StaticCodeCache(64));

        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, first, _releaseSpec);

        // Resolve twice so the second answer is the one the memo serves.
        Assert.That(repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(first));
        Assert.That(repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(first));

        Snapshot original = stateProvider.TakeSnapshot();
        stateProvider.InsertCode(TestItem.AddressA, second, _releaseSpec);

        Assert.That(repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(second));

        stateProvider.Restore(original);

        Assert.That(repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(first));
    }

    /// <summary>Two accounts sharing a code hash share its body; a different one must not be confused.</summary>
    [Test]
    public void Different_accounts_resolve_their_own_code()
    {
        byte[] shared = [(byte)Instruction.STOP];
        byte[] other = [(byte)Instruction.JUMPDEST, (byte)Instruction.STOP];

        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        CacheCodeInfoRepository repository = new(stateProvider, NoPrecompiles(), new StaticCodeCache(64));

        foreach (Address address in (Address[])[TestItem.AddressA, TestItem.AddressB, TestItem.AddressC])
        {
            stateProvider.CreateAccount(address, 0);
        }

        stateProvider.InsertCode(TestItem.AddressA, shared, _releaseSpec);
        stateProvider.InsertCode(TestItem.AddressB, shared, _releaseSpec);
        stateProvider.InsertCode(TestItem.AddressC, other, _releaseSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(shared));
            Assert.That(repository.GetCachedCodeInfo(TestItem.AddressC, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(other));
            Assert.That(repository.GetCachedCodeInfo(TestItem.AddressB, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(shared));
            Assert.That(repository.GetCachedCodeInfo(TestItem.AddressC, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(other));
            // Repeat after alternating to pin that a hit still answers for the right code hash.
            Assert.That(repository.GetCachedCodeInfo(TestItem.AddressC, false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(other));
        }
    }

    [Test]
    public void Recent_code_info_memo_keeps_four_hashes_and_evicts_round_robin()
    {
        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE];
        byte[][] codes =
        [
            [(byte)Instruction.STOP],
            [(byte)Instruction.JUMPDEST, (byte)Instruction.STOP],
            [(byte)Instruction.PUSH1, 1, (byte)Instruction.STOP],
            [(byte)Instruction.PUSH1, 2, (byte)Instruction.STOP],
            [(byte)Instruction.PUSH1, 3, (byte)Instruction.STOP],
        ];

        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        CountingCodeCache cache = new();
        CacheCodeInfoRepository repository = new(stateProvider, NoPrecompiles(), cache);
        for (int i = 0; i < addresses.Length; i++)
        {
            stateProvider.CreateAccount(addresses[i], 0);
            stateProvider.InsertCode(addresses[i], codes[i], _releaseSpec);
        }

        for (int i = 0; i < MemoCapacityForTests; i++)
        {
            Assert.That(repository.GetCachedCodeInfo(addresses[i], false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(codes[i]));
        }
        Assert.That(cache.GetCount, Is.EqualTo(4));

        for (int i = 0; i < MemoCapacityForTests; i++)
        {
            Assert.That(repository.GetCachedCodeInfo(addresses[i], false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(codes[i]));
        }
        Assert.That(cache.GetCount, Is.EqualTo(4));

        Assert.That(repository.GetCachedCodeInfo(addresses[4], false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(codes[4]));
        Assert.That(cache.GetCount, Is.EqualTo(5));
        Assert.That(repository.GetCachedCodeInfo(addresses[0], false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(codes[0]));
        Assert.That(cache.GetCount, Is.EqualTo(6));
        Assert.That(repository.GetCachedCodeInfo(addresses[4], false, _releaseSpec, out _).CodeSpan.ToArray(), Is.EqualTo(codes[4]));
        Assert.That(cache.GetCount, Is.EqualTo(6));
    }

    [Test]
    public void Alternating_memo_entries_refresh_their_shared_cache_tickers_independently()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        byte[][] alternatingCodes =
        [
            [(byte)Instruction.STOP],
            [(byte)Instruction.JUMPDEST, (byte)Instruction.STOP],
        ];
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.CreateAccount(TestItem.AddressB, 0);
        stateProvider.InsertCode(TestItem.AddressA, alternatingCodes[0], _releaseSpec);
        stateProvider.InsertCode(TestItem.AddressB, alternatingCodes[1], _releaseSpec);

        CountingCodeCache cache = new();
        CacheCodeInfoRepository repository = new(stateProvider, NoPrecompiles(), cache);
        repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _);
        repository.GetCachedCodeInfo(TestItem.AddressB, false, _releaseSpec, out _);

        for (int i = 0; i < 32; i++)
        {
            repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _);
            repository.GetCachedCodeInfo(TestItem.AddressB, false, _releaseSpec, out _);
        }
        Assert.That(cache.GetCount, Is.EqualTo(2));

        for (int i = 0; i < 32; i++)
        {
            repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _);
            repository.GetCachedCodeInfo(TestItem.AddressB, false, _releaseSpec, out _);
        }
        Assert.That(cache.GetCount, Is.EqualTo(4));
    }

    [Test]
    public void Noop_code_cache_keeps_recording_each_world_state_code_read()
    {
        CountingWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        byte[] code = [(byte)Instruction.STOP];
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);

        CacheCodeInfoRepository repository = new(stateProvider, NoPrecompiles(), NoopCodeCache.Instance);
        for (int i = 0; i < 2; i++)
        {
            Assert.That(repository.GetCachedCodeInfo(TestItem.AddressA, false, _releaseSpec, out _).CodeSpan.ToArray(),
                Is.EqualTo(code));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateProvider.CodeReads, Is.EqualTo(2));
            Assert.That(stateProvider.BytecodeAccesses, Is.EqualTo(2));
        }
    }

    /// <summary>A cached instance must not be re-pointed at a different code hash.</summary>
    /// <remarks>Memo lookup trusts the stamped hash, so a stamp that is not the keccak of the body could
    /// serve the wrong contract with no diagnostic.</remarks>
    [Test]
    public void Cached_code_cannot_be_re_stamped_with_another_hash()
    {
        byte[] code = [(byte)Instruction.STOP];
        CodeInfo codeInfo = new(code);
        StaticCodeCache cache = new(64);

        cache.Set(ValueKeccak.Compute(code), codeInfo);

        Assert.That(() => cache.Set(ValueKeccak.Compute([(byte)Instruction.JUMPDEST]), codeInfo),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Ordinary_address_is_not_taken_for_a_precompile()
    {
        // Sixteen leading zero bytes is what makes an address a candidate; this has none.
        Assert.That(TestItem.AddressA.CouldBePrecompile(), Is.False);
        Assert.That(_releaseSpec.IsPrecompile(TestItem.AddressA), Is.False);

        // Zero clears the shape guard — index 0 — so only the set can reject it.
        Assert.That(Address.Zero.CouldBePrecompile(), Is.True);
        Assert.That(_releaseSpec.IsPrecompile(Address.Zero), Is.False);
    }

    public static IEnumerable<TestCaseData> NotDelegationCodeCases()
    {
        byte[] rndAddress = new byte[20];
        TestContext.CurrentContext.Random.NextBytes(rndAddress);
        //Change first byte of the delegation header
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. rndAddress];
        code[0] = TestContext.CurrentContext.Random.NextByte(0xee);
        yield return new TestCaseData(code).SetName("Corrupted first byte of delegation header");
        //Change second byte of the delegation header
        code = [.. Eip7702Constants.DelegationHeader, .. rndAddress];
        code[1] = TestContext.CurrentContext.Random.NextByte(0x2, 0xff);
        yield return new TestCaseData(code).SetName("Corrupted second byte of delegation header");
        //Change third byte of the delegation header
        code = [.. Eip7702Constants.DelegationHeader, .. rndAddress];
        code[2] = TestContext.CurrentContext.Random.NextByte(0x1, 0xff);
        yield return new TestCaseData(code).SetName("Corrupted third byte of delegation header");
        code = [.. Eip7702Constants.DelegationHeader, .. new byte[21]];
        yield return new TestCaseData(code).SetName("Address too long (21 bytes)");
        code = [.. Eip7702Constants.DelegationHeader, .. new byte[19]];
        yield return new TestCaseData(code).SetName("Address too short (19 bytes)");
    }

    [TestCaseSource(nameof(NotDelegationCodeCases))]
    public void TryGetDelegation_CodeIsNotDelegation_ReturnsFalse(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable _scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        EthereumCodeInfoRepository sut = new(stateProvider);

        Assert.That(sut.TryGetDelegation(TestItem.AddressA, _releaseSpec, out _), Is.EqualTo(false));
    }

    [TestCase(false, TestName = "Missing account")]
    [TestCase(true, TestName = "Existing empty-code account")]
    public void TryGetDelegation_AccountWithoutCode_DoesNotLoadCodeInfo(bool createAccount)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        if (createAccount)
        {
            stateProvider.CreateAccount(TestItem.AddressA, 0);
        }

        bool codeInfoLoaderCalled = false;
        CodeInfoRepository sut = new(stateProvider, new EthereumPrecompileProvider(), (_, _, _) =>
        {
            codeInfoLoaderCalled = true;
            return CodeInfo.Empty;
        });

        Assert.That(sut.TryGetDelegation(TestItem.AddressA, _releaseSpec, out _), Is.False);

        Assert.That(codeInfoLoaderCalled, Is.False);
    }

    public static IEnumerable<TestCaseData> DelegationCodeCases()
    {
        byte[] address = new byte[20];
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. address];
        yield return new TestCaseData(code).SetName("Valid delegation with zero address");
        TestContext.CurrentContext.Random.NextBytes(address);
        code = [.. Eip7702Constants.DelegationHeader, .. address];
        yield return new TestCaseData(code).SetName("Valid delegation with random address");
    }

    [TestCaseSource(nameof(DelegationCodeCases))]
    public void TryGetDelegation_CodeTryGetDelegation_ReturnsTrue(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable _scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        EthereumCodeInfoRepository sut = new(stateProvider);

        Assert.That(sut.TryGetDelegation(TestItem.AddressA, _releaseSpec, out _), Is.EqualTo(true));
    }

    [TestCaseSource(nameof(DelegationCodeCases))]
    public void TryGetDelegation_CodeTryGetDelegation_CorrectDelegationAddressIsSet(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        EthereumCodeInfoRepository sut = new(stateProvider);

        sut.TryGetDelegation(TestItem.AddressA, _releaseSpec, out Address result);

        Assert.That(result, Is.EqualTo(new Address(code.Slice(3, Address.Size))));
    }

    [TestCaseSource(nameof(DelegationCodeCases))]
    public void GetCachedCodeInfo_CodeTryGetDelegation_ReturnsCodeOfDelegation(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        Address delegationAddress = new(code.Slice(3, Address.Size));
        stateProvider.CreateAccount(delegationAddress, 0);
        byte[] delegationCode = new byte[32];
        stateProvider.InsertCode(delegationAddress, delegationCode, _releaseSpec);
        EthereumCodeInfoRepository sut = new(stateProvider);

        CodeInfo result = sut.GetCachedCodeInfo(TestItem.AddressA, _releaseSpec);
        Assert.That(result.CodeSpan.ToArray(), Is.EqualTo(delegationCode));
    }

    [TestCaseSource(nameof(NotDelegationCodeCases))]
    public void GetCachedCodeInfo_CodeIsNotDelegation_ReturnsCodeOfAddress(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);

        EthereumCodeInfoRepository sut = new(stateProvider);

        Assert.That(sut.GetCachedCodeInfo(TestItem.AddressA, _releaseSpec), Is.EqualTo(new CodeInfo(code)));
    }
}
