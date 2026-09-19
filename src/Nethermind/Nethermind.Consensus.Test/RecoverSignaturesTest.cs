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
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
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
    public void RecoverData_SenderRecoveredButAuthorityMissing_RecoversAuthority()
    {
        PrivateKey signer = TestItem.PrivateKeyA;
        PrivateKey authority = TestItem.PrivateKeyB;
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithAuthorizationCode(_ecdsa.Sign(authority, 0, Address.Zero, 0))
            .SignedAndResolved(signer)
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

        Assert.That(tx.AuthorizationList[0].Authority, Is.EqualTo(authority.Address));
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

#nullable enable

    [Test]
    public async Task RecoverDataAsync_WhileInFlight_RecoverDataReturnsWithoutWaiting()
    {
        using ManualResetEventSlim gate = new();
        GatedEcdsa ecdsa = new(_ecdsa, gate, passThrough: 0);
        Transaction[] txs =
        [
            Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithSenderAddress(null).TestObject,
            Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithSenderAddress(null).TestObject,
        ];
        // The block constructor copies the array, as the engine payload path does: the registry must not depend on array identity.
        Block block = new(Build.A.BlockHeader.TestObject, txs, []);
        RecoverSignatures sut = new(ecdsa, CreateSpecProvider(), Substitute.For<ILogManager>());

        Task recovery = sut.RecoverDataAsync(block.Hash!, txs, ReleaseSpecSubstitute.Create());

        Assert.That(RecoverSignatures.IsRecoveryInFlight(block.Hash), Is.True);
        sut.RecoverData(block);
        Assert.That(txs[0].SenderAddress, Is.Null, "the pipeline step must not recover behind the in-flight task");

        gate.Set();
        await recovery;

        Assert.That(txs.Select(tx => tx.SenderAddress), Is.EqualTo(new[] { TestItem.AddressA, TestItem.AddressB }));
        Assert.That(RecoverSignatures.IsRecoveryInFlight(block.Hash), Is.False);
    }

    [Test]
    public async Task WaitForLeadingSenders_ReturnsOnceTheLeadingBatchIsRecovered()
    {
        int leading = Environment.ProcessorCount;
        using ManualResetEventSlim gate = new();
        GatedEcdsa ecdsa = new(_ecdsa, gate, passThrough: leading);
        Transaction[] txs = Enumerable.Range(0, leading + 2)
            .Select(nonce => Build.A.Transaction.WithNonce((ulong)nonce).SignedAndResolved(TestItem.PrivateKeyA).WithSenderAddress(null).TestObject)
            .ToArray();
        RecoverSignatures sut = new(ecdsa, CreateSpecProvider(), Substitute.For<ILogManager>());

        Task recovery = sut.RecoverDataAsync(TestItem.KeccakA, txs, ReleaseSpecSubstitute.Create());
        Task waited = Task.Run(() => RecoverSignatures.WaitForLeadingSenders(TestItem.KeccakA, txs));

        Assert.That(await Task.WhenAny(waited, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(waited), "waiting must end once the leading senders are in, not when recovery completes");
        Assert.That(txs.Take(leading).All(tx => tx.SenderAddress is not null), Is.True);

        gate.Set();
        await recovery;
        Assert.That(txs.All(tx => tx.SenderAddress == TestItem.AddressA), Is.True);
    }

    private static ISpecProvider CreateSpecProvider()
    {
        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        return specProvider;
    }

    /// <summary>Delegates to a real ecdsa, letting the first <c>passThrough</c> address recoveries through and parking the rest on a gate.</summary>
    private sealed class GatedEcdsa(IEthereumEcdsa inner, ManualResetEventSlim gate, int passThrough) : IEthereumEcdsa
    {
        private int _recoveries;

        public ulong ChainId => inner.ChainId;

        public Address? RecoverAddress(Signature signature, in ValueHash256 message)
        {
            if (Interlocked.Increment(ref _recoveries) > passThrough) gate.Wait();
            return inner.RecoverAddress(signature, in message);
        }

        public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => inner.Sign(privateKey, in message);
        public PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverPublicKey(signature, in message);
        public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverCompressedPublicKey(signature, in message);
    }
}
