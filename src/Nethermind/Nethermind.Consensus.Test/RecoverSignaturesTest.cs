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
using System.Linq;
using Nethermind.Core.Test;

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
        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
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
    public void RecoverData_FirstSenderAlreadyRecovered_RecoversRemainingSenders()
    {
        PrivateKey signerA = TestItem.PrivateKeyA;
        PrivateKey signerB = TestItem.PrivateKeyB;
        Transaction recovered = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithNonce(0)
            .SignedAndResolved(signerA)
            .TestObject;
        Transaction notRecovered = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithNonce(1)
            .SignedAndResolved(signerB)
            .WithSenderAddress(null)
            .TestObject;

        Block block = Build.A.Block
            .WithTransactions([recovered, notRecovered])
            .TestObject;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        RecoverSignatures sut = new(
            _ecdsa,
            specProvider,
            Substitute.For<ILogManager>());

        sut.RecoverData(block);

        Assert.That(notRecovered.SenderAddress, Is.EqualTo(signerB.Address));
    }
}
