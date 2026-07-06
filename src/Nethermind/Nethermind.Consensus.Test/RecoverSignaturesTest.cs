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
    public void RecoverData_CalledWhileRecoveryStartedByAnotherCallerIsInFlight_JoinsAndSeesAllSendersRecovered()
    {
        // Scenario (regression for the newPayload prewarm race):
        // 1. A background caller (NewPayloadHandler) starts RecoverData; parallel recovery is
        //    held mid-flight by a gate, so an arbitrary subset of senders may be set.
        // 2. A second caller (the processing path) invokes RecoverData for the same block.
        //    It must join the in-flight work instead of trusting the first-tx shortcut.
        // 3. After the gate opens, the second caller returns only when every sender is recovered.
        Transaction[] txs = Enumerable.Range(0, 8)
            .Select(i => Build.A.Transaction
                .WithNonce((ulong)i)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .WithSenderAddress(null)
                .TestObject)
            .ToArray();
        Block block = Build.A.Block.WithTransactions(txs).TestObject;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        using ManualResetEventSlim recoveryGate = new(initialState: false);
        using ManualResetEventSlim firstRecoveryStarted = new(initialState: false);
        GatedEcdsa gatedEcdsa = new(_ecdsa, recoveryGate, firstRecoveryStarted);
        RecoverSignatures sut = new(gatedEcdsa, specProvider, Substitute.For<ILogManager>());

        Task backgroundRecovery = Task.Run(() => sut.RecoverData(block));
        firstRecoveryStarted.Wait();

        Assert.That(txs.Any(static tx => tx.SenderAddress is null), Is.True,
            "precondition: recovery is mid-flight, at least one sender must still be unrecovered");

        Task joiningCaller = Task.Run(() => sut.RecoverData(block));

        Assert.That(joiningCaller.IsCompleted, Is.False,
            "the joining caller must block on the in-flight recovery, not exit via the first-tx shortcut");

        recoveryGate.Set();
        Task.WaitAll(backgroundRecovery, joiningCaller);

        Assert.That(txs.Select(static tx => tx.SenderAddress),
            Is.All.EqualTo(TestItem.PrivateKeyA.Address),
            "after the join returns every transaction must have its sender recovered");
    }

    private sealed class GatedEcdsa(IEthereumEcdsa inner, ManualResetEventSlim gate, ManualResetEventSlim firstStarted) : IEthereumEcdsa
    {
        public ulong ChainId => inner.ChainId;

        public Address RecoverAddress(Signature signature, in ValueHash256 message)
        {
            firstStarted.Set();
            gate.Wait();
            return inner.RecoverAddress(signature, in message);
        }

        public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => inner.Sign(privateKey, in message);
        public PublicKey RecoverPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverPublicKey(signature, in message);
        public CompressedPublicKey RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverCompressedPublicKey(signature, in message);
    }
}
