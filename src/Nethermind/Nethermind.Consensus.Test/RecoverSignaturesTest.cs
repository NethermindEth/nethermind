// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Consensus.Processing;
using NSubstitute;
using Nethermind.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Test;
[TestFixture]
public class RecoverSignaturesTest
{
    private static readonly IEthereumEcdsa _ecdsa = new EthereumEcdsa(BlockchainIds.GenericNonRealNetwork);

    [Test]
    public void RecoverData_SenderIsNotRecoveredAndNotInPool_SenderAndAuthorityIsRecovered()
    {
        PrivateKey signer = TestItem.PrivateKeyA;
        PrivateKey authority = TestItem.PrivateKeyB;
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithAuthorizationCode(_ecdsa.Sign(authority, 0, Address.Zero, 0))
            .SignedAndResolved(signer)
            .WithSenderAddress(null)
            .TestObject;

        Block block = Build.A.Block
            .WithTransactions([tx])
            .TestObject;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsAuthorizationListEnabled.Returns(true);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        RecoverSignatures sut = new(
            _ecdsa,
            specProvider,
            Substitute.For<ILogManager>());

        sut.RecoverData(block);

        Assert.That(tx.SenderAddress, Is.EqualTo(signer.Address));
        Assert.That(tx.AuthorizationList.First().Authority, Is.EqualTo(authority.Address));
    }

    [Test]
    public void RecoverData_TxIsInPool_SenderAndAuthoritiesIsSetToSameAsInPool()
    {
        PrivateKey signer = TestItem.PrivateKeyA;
        PrivateKey poolSender = TestItem.PrivateKeyB;
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithAuthorizationCode
            ([
                new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[65]), null),
                new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[65]), null),
            ])
            .SignedAndResolved(signer)
            .WithSenderAddress(null)
            .TestObject;

        Block block = Build.A.Block
            .WithTransactions([tx])
            .TestObject;

        Transaction txInPool = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithAuthorizationCode
            ([
                new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[65]), poolSender.Address),
                new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[65]), poolSender.Address),
            ])
            .SignedAndResolved(poolSender)
            .TestObject;
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool
            .TryGetPendingTransaction(Arg.Any<Hash256>(), out Arg.Any<Transaction>())
            .Returns(x =>
            {
                x[1] = txInPool;
                return true;
            });
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsAuthorizationListEnabled.Returns(true);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        RecoverSignatures sut = new(
            _ecdsa,
            specProvider,
            Substitute.For<ILogManager>());

        sut.RecoverData(block);

        Assert.That(tx.SenderAddress, Is.EqualTo(poolSender.Address));
        Assert.That(tx.AuthorizationList.Select(a => a.Authority), Is.All.EqualTo(poolSender.Address));
    }
}
