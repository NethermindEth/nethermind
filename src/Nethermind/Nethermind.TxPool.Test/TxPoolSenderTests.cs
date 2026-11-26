// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class TxPoolSenderTests
{
    [Test]
    public void SendTransaction_FrameTransaction_NotRejectedAsSignFailed()
    {
        TxPoolSender sender = BuildSender(out INonceManager _);

        (Hash256 _, AcceptTxResult? result) = sender.SendTransaction(FrameTx(nonce: 0), TxHandlingOptions.None).Result;

        Assert.That(result, Is.Not.EqualTo(AcceptTxResult.SignFailed));
    }

    /// <remarks>An EIP-8250 keyed sequence is not an account nonce, so recording one as consumed would hand the next
    /// managed-nonce reservation a nonce above the account's, leaving whatever is built on it unmineable.</remarks>
    [TestCase(true, 0UL, TestName = "a keyed nonce_seq reserves no account nonce")]
    [TestCase(false, 1UL, TestName = "an account-domain nonce still does")]
    public void SendTransaction_ReservesTheAccountNonce_OnlyForTheAccountDomain(bool keyed, ulong expectedReservation)
    {
        TxPoolSender sender = BuildSender(out INonceManager nonceManager);
        Transaction tx = FrameTx(nonce: 0, nonceKeys: keyed ? [1] : null);

        (Hash256 _, AcceptTxResult? result) = sender.SendTransaction(tx, TxHandlingOptions.None).Result;
        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted), "the submission must be accepted, or this pins nothing");

        IPendingTxsBySender pendingTxs = Substitute.For<IPendingTxsBySender>();
        pendingTxs.ContainsTx(tx.Hash!, tx.Type).Returns(true);
        using NonceLocker locker = nonceManager.ReserveNonce(TestItem.AddressA, pendingTxs, out ulong reservedNonce);
        Assert.That(reservedNonce, Is.EqualTo(expectedReservation));
    }

    /// <remarks><c>eth_sendTransaction</c> reaches this whenever the caller supplies no nonce; overwriting nonce_seq
    /// with an account nonce would submit a sequence the sender never selected.</remarks>
    [Test]
    public void SendTransaction_ManagedNonce_DoesNotOverwriteAKeyedSequence()
    {
        TxPoolSender sender = BuildSender(out INonceManager _);
        Transaction tx = FrameTx(nonce: 7, nonceKeys: [1]);

        (Hash256 _, AcceptTxResult? result) = sender.SendTransaction(tx, TxHandlingOptions.ManagedNonce).Result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.Nonce, Is.EqualTo(7UL));
        }
    }

    public enum Signing { BySender, ByAnotherAccount, ReplacedWhenSealed }

    /// <remarks>
    /// The pool keeps a sender it is given, and every node runs a transaction as the account its signature recovers.
    /// A signature replaced when the transaction is sealed, as with a managed nonce, is the sealer's.
    /// </remarks>
    [TestCase(Signing.BySender, true)]
    [TestCase(Signing.ByAnotherAccount, false)]
    [TestCase(Signing.ReplacedWhenSealed, true)]
    public void Submission_with_a_given_sender_is_accepted_only_when_signed_by_it(Signing signing, bool accepted)
    {
        EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);
        ITxSigner signer = Substitute.For<ITxSigner>();
        signer.TrySign(Arg.Any<Transaction>()).Returns(call =>
        {
            ecdsa.Sign(TestItem.PrivateKeyB, call.Arg<Transaction>());
            return true;
        });
        TxPoolSender sender = BuildSender(out INonceManager _, txPool, signer, ecdsa);
        Transaction tx = Build.A.Transaction.WithNonce(0).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
        if (signing != Signing.BySender) tx.SenderAddress = TestItem.AddressB;

        (Hash256 _, AcceptTxResult? result) = sender.SendTransaction(tx,
            signing == Signing.ReplacedWhenSealed ? TxHandlingOptions.ManagedNonce : TxHandlingOptions.None).Result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.Invalid));
            Assert.That(txPool.ReceivedCalls().Any(static call => call.GetMethodInfo().Name == nameof(ITxPool.SubmitTx)), Is.EqualTo(accepted), "submitted to the pool");
        }
    }

    public enum GivenSender { Signer, AddressRecoveredWithoutTheSignatureChainId, None }

    /// <remarks>
    /// Where the spec does not validate chain ids, the pool accepts a legacy signature made for another chain, and block
    /// processing recovers its signer over the chain id the signature carries.
    /// </remarks>
    [TestCase(GivenSender.Signer, true)]
    [TestCase(GivenSender.AddressRecoveredWithoutTheSignatureChainId, false)]
    [TestCase(GivenSender.None, true)]
    public void Legacy_signature_for_another_chain_is_sent_only_as_its_signer_where_chain_ids_are_not_validated(GivenSender given, bool accepted)
    {
        EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);
        TxPoolSender sender = BuildSender(out INonceManager _, txPool, ecdsa: ecdsa,
            spec: new OverridableReleaseSpec(SpuriousDragon.Instance) { ValidateChainId = false });
        Transaction tx = Build.A.Transaction.WithNonce(0).Signed(new EthereumEcdsa(TestBlockchainIds.ChainId + 1), TestItem.PrivateKeyA).TestObject;
        Address otherAddress = ecdsa.RecoverAddress(tx);
        tx.SenderAddress = given switch
        {
            GivenSender.Signer => TestItem.AddressA,
            GivenSender.AddressRecoveredWithoutTheSignatureChainId => otherAddress,
            _ => null
        };

        (Hash256 _, AcceptTxResult? result) = sender.SendTransaction(tx, TxHandlingOptions.None).Result;

        Address[] submitted = [.. txPool.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name == nameof(ITxPool.SubmitTx))
            .Select(static call => ((Transaction)call.GetArguments()[0]).SenderAddress)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherAddress, Is.Not.EqualTo(TestItem.AddressA), "precondition: without its chain id the signature recovers another address");
            Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.Invalid));
            Assert.That(submitted, Is.EqualTo(accepted ? new[] { TestItem.AddressA } : Array.Empty<Address>()), "the senders submitted to the pool");
        }
    }

    /// <remarks>A frame transaction has no r/s signature: its sender is part of its payload, and r/s it carries are not used.</remarks>
    [Test]
    public void Frame_transaction_with_r_s_of_another_account_is_submitted_as_its_sender()
    {
        EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);
        TxPoolSender sender = BuildSender(out INonceManager _, txPool, ecdsa: ecdsa);
        Transaction tx = FrameTx(nonce: 0);
        tx.Signature = Build.A.Transaction.Signed(ecdsa, TestItem.PrivateKeyB).TestObject.Signature;

        (Hash256 _, AcceptTxResult? result) = sender.SendTransaction(tx, TxHandlingOptions.None).Result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(txPool.ReceivedCalls().Any(static call => call.GetMethodInfo().Name == nameof(ITxPool.SubmitTx)), Is.True, "submitted to the pool");
            Assert.That(tx.SenderAddress, Is.EqualTo(TestItem.AddressA));
        }
    }

    private static TxPoolSender BuildSender(out INonceManager nonceManager, ITxPool txPool = null, ITxSigner signer = null, IEthereumEcdsa ecdsa = null,
        IReleaseSpec spec = null)
    {
        if (txPool is null)
        {
            txPool = Substitute.For<ITxPool>();
            txPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);
        }

        TxSealer sealer = new(signer ?? Substitute.For<ITxSigner>(), Timestamper.Default);

        // NonceLocker is a ref struct, so INonceManager cannot be substituted; use the real one.
        IChainHeadInfoProvider chainHeadInfoProvider = Substitute.For<IChainHeadInfoProvider>();
        chainHeadInfoProvider.ReadOnlyStateProvider.GetNonce(TestItem.AddressA).Returns(0UL);
        nonceManager = new NonceManager(chainHeadInfoProvider, Substitute.For<IStateHeaderProvider>(), Substitute.For<IStateReader>());

        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetCurrentHeadSpec().Returns(spec ?? Prague.Instance);

        return new TxPoolSender(txPool, sealer, nonceManager, ecdsa ?? Substitute.For<IEthereumEcdsa>(), specProvider);
    }

    private static Transaction FrameTx(ulong nonce, UInt256[] nonceKeys = null) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Nonce = nonce,
        ChainId = 1,
        GasLimit = 1_000_000,
        GasPrice = 1,
        DecodedMaxFeePerGas = 100,
        Frames =
        [
            new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>()),
        ],
        FrameSignatures = [],
        NonceKeys = nonceKeys,
    };
}
