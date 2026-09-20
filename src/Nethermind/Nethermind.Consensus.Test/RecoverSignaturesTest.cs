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

    private const int DrainTimeoutMs = 10_000;
    private const int PollMs = 5;

    [Test]
    public void StartRecovery_WhileRunning_PipelineStepLeavesTheBlockAlone()
    {
        using ManualResetEventSlim gate = new();
        Transaction[] txs =
        [
            Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).WithSenderAddress(null).TestObject,
            Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB).WithSenderAddress(null).TestObject,
        ];
        // The block constructor copies the array, as the engine payload path does.
        Block block = new(Build.A.BlockHeader.TestObject, txs, []);
        RecoverSignatures sut = CreateSut(gate, txs[0], txs[1]);

        sut.StartRecovery(block.Hash!, txs, ReleaseSpecSubstitute.Create());

        Assert.That(sut.IsRecoveryInFlight(block.Transactions), Is.True);
        sut.RecoverData(block);
        Assert.That(txs[0].SenderAddress, Is.Null, "the pipeline step must not recover behind the running recovery");

        ReleaseAndDrain(gate, sut, txs);
        Assert.That(txs.Select(static tx => tx.SenderAddress), Is.EqualTo(new[] { TestItem.AddressA, TestItem.AddressB }));
    }

    [Test]
    public void StartRecovery_ResentPayload_DoesNotStartASecondRecovery()
    {
        using ManualResetEventSlim gate = new();
        Transaction[] first = SignedTransactions(2);
        Transaction[] resent = SignedTransactions(2);
        RecoverSignatures sut = CreateSut(gate, first[0], first[1]);

        sut.StartRecovery(TestItem.KeccakA, first, ReleaseSpecSubstitute.Create());
        sut.StartRecovery(TestItem.KeccakA, resent, ReleaseSpecSubstitute.Create());

        Assert.That(sut.IsRecoveryInFlight(first), Is.True);
        Assert.That(sut.IsRecoveryInFlight(resent), Is.False, "the resent payload's own transactions stay with the pipeline");

        ReleaseAndDrain(gate, sut, first);
    }

    private static Transaction[] SignedTransactions(int count) =>
        Enumerable.Range(0, count)
            .Select(static nonce => Build.A.Transaction.WithNonce((ulong)nonce).SignedAndResolved(TestItem.PrivateKeyA).WithSenderAddress(null).TestObject)
            .ToArray();

    private static RecoverSignatures CreateSut(ManualResetEventSlim gate, params Transaction[] parked)
    {
        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        GatedEcdsa ecdsa = new(_ecdsa, gate, parked.Select(static tx => tx.Signature!).ToArray());
        return new RecoverSignatures(ecdsa, specProvider, Substitute.For<ILogManager>());
    }

    /// <summary>Lets the parked recoveries through and waits for the work item, so no thread outlives the gate.</summary>
    private static void ReleaseAndDrain(ManualResetEventSlim gate, RecoverSignatures sut, Transaction[] txs)
    {
        gate.Set();
        Assert.That(() => sut.IsRecoveryInFlight(txs), Is.False.After(DrainTimeoutMs, PollMs));
    }

    /// <summary>Delegates to a real ecdsa, parking the recoveries of the given signatures on a gate.</summary>
    private sealed class GatedEcdsa(IEthereumEcdsa inner, ManualResetEventSlim gate, Signature[] parked) : IEthereumEcdsa
    {
        public ulong ChainId => inner.ChainId;

        public Address? RecoverAddress(Signature signature, in ValueHash256 message)
        {
            foreach (Signature candidate in parked)
            {
                if (ReferenceEquals(candidate, signature))
                {
                    gate.Wait();
                    break;
                }
            }

            return inner.RecoverAddress(signature, in message);
        }

        public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => inner.Sign(privateKey, in message);
        public PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverPublicKey(signature, in message);
        public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverCompressedPublicKey(signature, in message);
    }
}
