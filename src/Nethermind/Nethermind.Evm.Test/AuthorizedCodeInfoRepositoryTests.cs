// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp.Eip7702;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using FluentAssertions;
using Nethermind.State;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs.Forks;
using Nethermind.Specs;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm.Test;

[TestFixture]
public class AuthorizedCodeInfoRepositoryTests
{
    [Test]
    public void InsertFromAuthorizations_AuthorityTupleIsCorrect_CodeIsInserted()
    {
        PrivateKey authority = TestItem.PrivateKeyA;
        ICodeInfoRepository mockCodeRepository = Substitute.For<ICodeInfoRepository>();
        mockCodeRepository
            .GetCachedCodeInfo(Arg.Any<IWorldState>(), authority.Address, Arg.Any<IReleaseSpec>())
            .Returns(new CodeInfo([]));
        AuthorizedCodeInfoRepository sut = new(mockCodeRepository, 1, NullLogger.Instance);
        var tuples = new[]
        {
            CreateAuthorizationTuple(authority, 1, TestItem.AddressB, (UInt256)0),
        };
        sut.InsertFromAuthorizations(Substitute.For<IWorldState>(), tuples, Substitute.For<IReleaseSpec>());

        sut.AuthorizedAddresses.Should().BeEquivalentTo([authority.Address]);
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
        ICodeInfoRepository mockCodeRepository = Substitute.For<ICodeInfoRepository>();
        mockCodeRepository
            .GetCachedCodeInfo(Arg.Any<IWorldState>(), Arg.Any<Address>(), Arg.Any<IReleaseSpec>())
            .Returns(new CodeInfo(Array.Empty<byte>()));
        AuthorizedCodeInfoRepository sut = new(mockCodeRepository, 1, NullLogger.Instance);

        sut.InsertFromAuthorizations(Substitute.For<IWorldState>(), tuples, Substitute.For<IReleaseSpec>());

        sut.AuthorizedAddresses.Count().Should().Be(expectedCount);
    }

    [Test]
    public void InsertFromAuthorizations_AuthorityHasCode_NoCodeIsInserted()
    {
        PrivateKey authority = TestItem.PrivateKeyA;
        ICodeInfoRepository mockCodeRepository = Substitute.For<ICodeInfoRepository>();
        mockCodeRepository
            .GetCachedCodeInfo(Arg.Any<IWorldState>(), authority.Address, Arg.Any<IReleaseSpec>())
            .Returns(new CodeInfo([(byte)0x0]));
        AuthorizedCodeInfoRepository sut = new(mockCodeRepository, 1, NullLogger.Instance);
        var tuples = new[]
        {
            CreateAuthorizationTuple(authority, 1, TestItem.AddressB, (UInt256)0),
        };
        sut.InsertFromAuthorizations(Substitute.For<IWorldState>(), tuples, Substitute.For<IReleaseSpec>());

        sut.AuthorizedAddresses.Count().Should().Be(0);
    }

    [Test]
    public void ClearAuthorizations_HasAuthorizedAddresses_AuthorizationsAreClear()
    {
        ICodeInfoRepository mockCodeRepository = Substitute.For<ICodeInfoRepository>();
        mockCodeRepository
            .GetCachedCodeInfo(Arg.Any<IWorldState>(), Arg.Any<Address>(), Arg.Any<IReleaseSpec>())
            .Returns(new CodeInfo(Array.Empty<byte>()));
        AuthorizedCodeInfoRepository sut = new(mockCodeRepository, 1, NullLogger.Instance);
        var tuples = new[]
        {
            CreateAuthorizationTuple(TestItem.PrivateKeyA, 1, TestItem.AddressB, (UInt256)0),
            CreateAuthorizationTuple(TestItem.PrivateKeyB, 1, TestItem.AddressB, (UInt256)0),
        };
        sut.InsertFromAuthorizations(Substitute.For<IWorldState>(), tuples, Substitute.For<IReleaseSpec>());

        sut.AuthorizedAddresses.Count().Should().Be(2);

        sut.ClearAuthorizations();

        sut.AuthorizedAddresses.Count().Should().Be(0);
    }

    private static AuthorizationTuple CreateAuthorizationTuple(PrivateKey signer, ulong chainId, Address codeAddress, UInt256? nonce)
    {
        AuthorizationTupleDecoder decoder = new();
        RlpStream rlp = decoder.EncodeForCommitMessage(chainId, codeAddress, nonce);
        Span<byte> code = stackalloc byte[rlp.Length + 1];
        code[0] = Eip7702Constants.Magic;
        rlp.Data.AsSpan().CopyTo(code.Slice(1));
        EthereumEcdsa ecdsa = new(1, new OneLoggerLogManager(NullLogger.Instance));
        Signature sig = ecdsa.Sign(signer, Keccak.Compute(code));

        return new AuthorizationTuple(chainId, codeAddress, nonce, sig, signer.Address);
    }
}
