// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;
using Nethermind.Core.Test.Builders;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Evm.State;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;

namespace Nethermind.Evm.Test;

[TestFixture, Parallelizable]
public class CodeInfoRepositoryTests
{
    private static readonly IReleaseSpec _releaseSpec;

    static CodeInfoRepositoryTests()
    {
        _releaseSpec = Substitute.For<IReleaseSpec>();
        _releaseSpec.Precompiles.Returns(FrozenSet<AddressAsKey>.Empty);
    }

    public static IEnumerable<object[]> NotDelegationCodeCases()
    {
        byte[] rndAddress = new byte[20];
        TestContext.CurrentContext.Random.NextBytes(rndAddress);
        //Change first byte of the delegation header
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. rndAddress];
        code[0] = TestContext.CurrentContext.Random.NextByte(0xee);
        yield return [code];
        //Change second byte of the delegation header
        code = [.. Eip7702Constants.DelegationHeader, .. rndAddress];
        code[1] = TestContext.CurrentContext.Random.NextByte(0x2, 0xff);
        yield return [code];
        //Change third byte of the delegation header
        code = [.. Eip7702Constants.DelegationHeader, .. rndAddress];
        code[2] = TestContext.CurrentContext.Random.NextByte(0x1, 0xff);
        yield return [code];
        code = [.. Eip7702Constants.DelegationHeader, .. new byte[21]];
        yield return [code];
        code = [.. Eip7702Constants.DelegationHeader, .. new byte[19]];
        yield return [code];
    }

    [TestCaseSource(nameof(NotDelegationCodeCases))]
    public void TryGetDelegation_CodeIsNotDelegation_ReturnsFalse(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using var _scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        EthereumCodeInfoRepository sut = new(stateProvider);

        sut.TryGetDelegation(TestItem.AddressA, _releaseSpec, out _).Should().Be(false);
    }


    public static IEnumerable<object[]> DelegationCodeCases()
    {
        byte[] address = new byte[20];
        byte[] code = [.. Eip7702Constants.DelegationHeader, .. address];
        yield return [code];
        TestContext.CurrentContext.Random.NextBytes(address);
        code = [.. Eip7702Constants.DelegationHeader, .. address];
        yield return [code];
    }

    [TestCaseSource(nameof(DelegationCodeCases))]
    public void TryGetDelegation_CodeTryGetDelegation_ReturnsTrue(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using var _scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        EthereumCodeInfoRepository sut = new(stateProvider);

        sut.TryGetDelegation(TestItem.AddressA, _releaseSpec, out _).Should().Be(true);
    }

    [TestCaseSource(nameof(DelegationCodeCases))]
    public void TryGetDelegation_CodeTryGetDelegation_CorrectDelegationAddressIsSet(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using var _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        EthereumCodeInfoRepository sut = new(stateProvider);

        Address result;
        sut.TryGetDelegation(TestItem.AddressA, _releaseSpec, out result);

        result.Should().Be(new Address(code.Slice(3, Address.Size)));
    }

    [TestCaseSource(nameof(DelegationCodeCases))]
    public void GetExecutableCodeHash_CodeTryGetDelegation_ReturnsHashOfDelegated(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using var _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        Address delegationAddress = new Address(code.Slice(3, Address.Size));
        byte[] delegationCode = new byte[32];
        stateProvider.CreateAccount(delegationAddress, 0);
        stateProvider.InsertCode(delegationAddress, delegationCode, _releaseSpec);

        EthereumCodeInfoRepository sut = new(stateProvider);

        sut.GetExecutableCodeHash(TestItem.AddressA, _releaseSpec).Should().Be(Keccak.Compute(code).ValueHash256);
    }

    [TestCaseSource(nameof(NotDelegationCodeCases))]
    public void GetExecutableCodeHash_CodeIsNotDelegation_ReturnsCodeHashOfAddress(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using var _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);

        EthereumCodeInfoRepository sut = new(stateProvider);

        sut.GetExecutableCodeHash(TestItem.AddressA, _releaseSpec).Should().Be(Keccak.Compute(code).ValueHash256);
    }

    [TestCaseSource(nameof(DelegationCodeCases))]
    public void GetCachedCodeInfo_CodeTryGetDelegation_ReturnsCodeOfDelegation(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using var _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);
        Address delegationAddress = new Address(code.Slice(3, Address.Size));
        stateProvider.CreateAccount(delegationAddress, 0);
        byte[] delegationCode = new byte[32];
        stateProvider.InsertCode(delegationAddress, delegationCode, _releaseSpec);
        EthereumCodeInfoRepository sut = new(stateProvider);

        CodeInfo result = sut.GetCachedCodeInfo(TestItem.AddressA, _releaseSpec);
        result.CodeSpan.ToArray().Should().BeEquivalentTo(delegationCode);
    }

    [TestCaseSource(nameof(NotDelegationCodeCases))]
    public void GetCachedCodeInfo_CodeIsNotDelegation_ReturnsCodeOfAddress(byte[] code)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using var _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(TestItem.AddressA, 0);
        stateProvider.InsertCode(TestItem.AddressA, code, _releaseSpec);

        EthereumCodeInfoRepository sut = new(stateProvider);

        sut.GetCachedCodeInfo(TestItem.AddressA, _releaseSpec).Should().BeEquivalentTo(new CodeInfo(code));
    }
}
