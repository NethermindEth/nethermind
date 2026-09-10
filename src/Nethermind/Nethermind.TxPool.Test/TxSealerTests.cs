// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class TxSealerTests
{
    [Test]
    public void TrySeal_FrameTransaction_SkipsSigning()
    {
        ITxSigner txSigner = Substitute.For<ITxSigner>();
        TxSealer sealer = new(txSigner, Substitute.For<ITimestamper>());

        Transaction frameTx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, Array.Empty<byte>())],
            FrameSignatures = [],
        };

        bool result = sealer.TrySeal(frameTx, TxHandlingOptions.None);

        Assert.That(result, Is.True, "frame transactions must seal without signing");
    }
}
