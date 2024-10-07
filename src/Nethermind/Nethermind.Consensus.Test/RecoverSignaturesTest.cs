// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Core.Crypto;
using Nethermind.Consensus.Processing;
using NSubstitute;
using Nethermind.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;
using System.Linq;

namespace Nethermind.Consensus.Test;
[TestFixture]
public class RecoverSignaturesTest
{
    private static readonly IEthereumEcdsa _ecdsa = new EthereumEcdsa(BlockchainIds.GenericNonRealNetwork);

    [Test]
    public void RecoverData_SenderIsNotRecoveredAndNotInPool_SenderIsRecovered()
    {
        PrivateKey signer = TestItem.PrivateKeyA;
        Transaction tx = Build.A.Transaction
          .SignedAndResolved(signer)
          .WithSenderAddress(null)
          .TestObject;

        Block block = Build.A.Block
            .WithTransactions([tx])
            .TestObject;

        RecoverSignatures sut = new(
            _ecdsa,
            NullTxPool.Instance,
            Substitute.For<ISpecProvider>(),
            Substitute.For<ILogManager>());

        sut.RecoverData(block);

        Assert.That(tx.SenderAddress, Is.EqualTo(signer.Address)); 
    }


    [Test]
    public void RecoverData_SenderIsNotRecovered_SenderIsRecovered()
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
        specProvider.GetSpec(Arg.Any<ForkActivation>()). IsAuthorizationListEnabled.Returns(true);
        RecoverSignatures sut = new(
            _ecdsa,
            NullTxPool.Instance,
            specProvider,
            Substitute.For<ILogManager>());

        sut.RecoverData(block);

        Assert.That(tx.SenderAddress, Is.EqualTo(signer.Address));
        Assert.That(tx.AuthorizationList.First().Authority, Is.EqualTo(authority.Address));
    }
}
