// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Test.Builders;
using FluentAssertions;
using Nethermind.State;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Trie.Pruning;
using System.Diagnostics.Tracing;

namespace Nethermind.Evm.Test;

[TestFixture]
public class CodeInfoRepositoryTests
{
    [Test]
    public void InsertFromAuthorizations_AuthorityTupleIsCorrect_CodeIsInserted()
    {
        PrivateKey authority = TestItem.PrivateKeyA;
        CodeInfoRepository sut = new(1);
        var tuples = new[]
        {
            CreateAuthorizationTuple(authority, 1, TestItem.AddressB, 0),
        };
        CodeInsertResult result = sut.InsertFromAuthorizations(Substitute.For<IWorldState>(), tuples, Substitute.For<IReleaseSpec>());

        result.AccessedAddresses.Should().BeEquivalentTo([authority.Address]);
    }

    public static IEnumerable<object[]> AuthorizationCases()
    {
        yield return new object[]
        {
            CreateAuthorizationTuple(TestItem.PrivateKeyA, 1, TestItem.AddressB, 0),
            true
        };
        yield return new object[]
        {
            //Wrong chain id
            CreateAuthorizationTuple(TestItem.PrivateKeyB, 2, TestItem.AddressB, 0),
            false
        };
        yield return new object[]
        {            
            //wrong nonce
            CreateAuthorizationTuple(TestItem.PrivateKeyC, 1, TestItem.AddressB, 1),
            false
        };
    }

    [TestCaseSource(nameof(AuthorizationCases))]
    public void InsertFromAuthorizations_MixOfCorrectAndWrongChainIdAndNonce_InsertsIfExpected(AuthorizationTuple tuple, bool shouldInsert)
    {
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        CodeInfoRepository sut = new(1);

        CodeInsertResult result = sut.InsertFromAuthorizations(stateProvider, [tuple], Substitute.For<IReleaseSpec>());

        Assert.That(stateProvider.HasCode(result.AccessedAddresses.First()), Is.EqualTo(shouldInsert));
    }

    [Test]
    public void InsertFromAuthorizations_AuthorityHasCode_NoCodeIsInserted()
    {
        PrivateKey authority = TestItem.PrivateKeyA;
        Address codeSource = TestItem.AddressB;
        IWorldState mockWorldState = Substitute.For<IWorldState>();
        mockWorldState.HasCode(authority.Address).Returns(true);
        mockWorldState.GetCode(authority.Address).Returns(new byte[32]);
        CodeInfoRepository sut = new(1);
        var tuples = new[]
        {
            CreateAuthorizationTuple(authority, 1, codeSource, 0),
        };

        sut.InsertFromAuthorizations(mockWorldState, tuples, Substitute.For<IReleaseSpec>());

        mockWorldState.DidNotReceive().InsertCode(Arg.Any<Address>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<IReleaseSpec>());
    }

    [Test]
    public void InsertFromAuthorizations_AuthorityHasDelegatedCode_CodeIsInserted()
    {
        PrivateKey authority = TestItem.PrivateKeyA;
        Address codeSource = TestItem.AddressB;
        IWorldState mockWorldState = Substitute.For<IWorldState>();
        mockWorldState.HasCode(authority.Address).Returns(true);
        byte[] code = new byte[23];
        Eip7702Constants.DelegationHeader.CopyTo(code);
        mockWorldState.GetCode(authority.Address).Returns(code);
        CodeInfoRepository sut = new(1);
        var tuples = new[]
        {
            CreateAuthorizationTuple(authority, 1, codeSource, 0),
        };

        sut.InsertFromAuthorizations(mockWorldState, tuples, Substitute.For<IReleaseSpec>());

        mockWorldState.Received().InsertCode(authority.Address, Arg.Any<Hash256>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<IReleaseSpec>(), Arg.Any<bool>());
    }

    [Test]
    public void InsertFromAuthorizations_AuthorityHasZeroNonce_NonceIsIncrementedByOne()
    {
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        PrivateKey authority = TestItem.PrivateKeyA;
        Address codeSource = TestItem.AddressB;
        stateProvider.CreateAccount(authority.Address, 0);
        CodeInfoRepository sut = new(1);
        var tuples = new[]
        {
            CreateAuthorizationTuple(authority, 1, codeSource, 0),
        };

        sut.InsertFromAuthorizations(stateProvider, tuples, Substitute.For<IReleaseSpec>());

        Assert.That(stateProvider.GetNonce(authority.Address), Is.EqualTo((UInt256)1));
    }

    [Test]
    public void InsertFromAuthorizations_FourAuthorizationInTotalButTwoAreInvalid_ResultContainsAllFour()
    {
        CodeInfoRepository sut = new(1);
        var tuples = new[]
        {
            CreateAuthorizationTuple(TestItem.PrivateKeyA, 1, TestItem.AddressF, 0),
            CreateAuthorizationTuple(TestItem.PrivateKeyB, 1, TestItem.AddressF, 0),
            CreateAuthorizationTuple(TestItem.PrivateKeyC, 2, TestItem.AddressF, 0),
            CreateAuthorizationTuple(TestItem.PrivateKeyD, 1, TestItem.AddressF, 1),
        };
        CodeInsertResult result = sut.InsertFromAuthorizations(Substitute.For<IWorldState>(), tuples, Substitute.For<IReleaseSpec>());

        result.AccessedAddresses.Should().BeEquivalentTo([TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD]);
    }

    [Test]
    public void InsertFromAuthorizations_AuthorizationsHasOneExistingAccount_ResultHaveOneRefund()
    {
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(trieStore, codeDb, LimboLogs.Instance);
        CodeInfoRepository sut = new(1);
        var tuples = new[]
        {
            CreateAuthorizationTuple(TestItem.PrivateKeyA, 1, TestItem.AddressF, 0),
            CreateAuthorizationTuple(TestItem.PrivateKeyB, 1, TestItem.AddressF, 0),
        };
        stateProvider.CreateAccount(TestItem.AddressA, 0);

        CodeInsertResult result = sut.InsertFromAuthorizations(stateProvider, tuples, Substitute.For<IReleaseSpec>());

        result.Refunds.Should().Be(1);
    }

    private static AuthorizationTuple CreateAuthorizationTuple(PrivateKey signer, ulong chainId, Address codeAddress, ulong nonce)
    {
        AuthorizationTupleDecoder decoder = new();
        RlpStream rlp = decoder.EncodeWithoutSignature(chainId, codeAddress, nonce);
        Span<byte> code = stackalloc byte[rlp.Length + 1];
        code[0] = Eip7702Constants.Magic;
        rlp.Data.AsSpan().CopyTo(code.Slice(1));
        EthereumEcdsa ecdsa = new(1);
        Signature sig = ecdsa.Sign(signer, Keccak.Compute(code));

        return new AuthorizationTuple(chainId, codeAddress, nonce, sig, signer.Address);
    }
}
