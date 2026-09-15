// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
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

        using NonceLocker locker = nonceManager.ReserveNonce(TestItem.AddressA, out ulong reservedNonce);
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

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(tx.Nonce, Is.EqualTo(7UL));
    }

    private static TxPoolSender BuildSender(out INonceManager nonceManager)
    {
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);

        TxSealer sealer = new(Substitute.For<ITxSigner>(), Timestamper.Default);

        // NonceLocker is a ref struct, so INonceManager cannot be substituted; use the real one.
        IAccountStateProvider accountStateProvider = Substitute.For<IAccountStateProvider>();
        accountStateProvider.GetNonce(TestItem.AddressA).Returns(0UL);
        nonceManager = new NonceManager(accountStateProvider);

        return new TxPoolSender(txPool, sealer, nonceManager, Substitute.For<IEthereumEcdsa>());
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
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>()),
        ],
        FrameSignatures = [],
        NonceKeys = nonceKeys,
    };
}
