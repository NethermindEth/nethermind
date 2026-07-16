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
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);

        ITxSigner txSigner = Substitute.For<ITxSigner>();

        TxSealer sealer = new(txSigner, Timestamper.Default);

        INonceManager nonceManager = Substitute.For<INonceManager>();

        IEthereumEcdsa ecdsa = Substitute.For<IEthereumEcdsa>();

        TxPoolSender sender = new(txPool, sealer, nonceManager, ecdsa);

        Transaction frameTx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Nonce = 0,
            ChainId = 1,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>()),
            ],
            FrameSignatures = [],
        };

        (Hash256 _, AcceptTxResult? result) = sender.SendTransaction(frameTx, TxHandlingOptions.None).Result;

        Assert.That(result, Is.Not.EqualTo(AcceptTxResult.SignFailed));
    }
}
