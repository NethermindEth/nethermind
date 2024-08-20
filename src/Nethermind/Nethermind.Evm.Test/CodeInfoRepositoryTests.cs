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
            CreateAuthorizationTuple(authority, 1, TestItem.AddressB, (UInt256)0),
        };
        IEnumerable<Address> result = sut.InsertFromAuthorizations(Substitute.For<IWorldState>(), tuples, Substitute.For<IReleaseSpec>());

        result.Should().BeEquivalentTo([authority.Address]);
    }

    public static IEnumerable<object[]> AuthorizationCases()
    {
        yield return new object[]
        {
            new[]
            {
                CreateAuthorizationTuple(TestItem.PrivateKeyA, 1, TestItem.AddressB, (UInt256)0),
                //Wrong chain id
                CreateAuthorizationTuple(TestItem.PrivateKeyA, 0, TestItem.AddressB, (UInt256)0),
                //wrong nonce
                CreateAuthorizationTuple(TestItem.PrivateKeyA, 1, TestItem.AddressB, (UInt256)1),
            }
            , 1
        };
    }

    [TestCaseSource(nameof(AuthorizationCases))]
    public void InsertFromAuthorizations_MixOfCorrectAndWrongChainIdAndNonce_InsertsExpectedCount(AuthorizationTuple[] tuples, int expectedCount)
    {
        CodeInfoRepository sut = new(1);

        IEnumerable<Address> result = sut.InsertFromAuthorizations(Substitute.For<IWorldState>(), tuples, Substitute.For<IReleaseSpec>());

        result.Count().Should().Be(expectedCount);
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
            CreateAuthorizationTuple(authority, 1, codeSource, (UInt256)0),
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
            CreateAuthorizationTuple(authority, 1, codeSource, (UInt256)0),
        };

        sut.InsertFromAuthorizations(mockWorldState, tuples, Substitute.For<IReleaseSpec>());

        mockWorldState.Received().InsertCode(authority.Address, Arg.Any<Hash256>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<IReleaseSpec>(), Arg.Any<bool>());
    }

    private static AuthorizationTuple CreateAuthorizationTuple(PrivateKey signer, ulong chainId, Address codeAddress, UInt256? nonce)
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
