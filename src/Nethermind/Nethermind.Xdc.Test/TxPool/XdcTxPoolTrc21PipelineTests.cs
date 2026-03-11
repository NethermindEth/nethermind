// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.TxPool;

internal class XdcTxPoolTrc21PipelineTests
{
    [Test]
    public async Task SubmitTx_ShouldAcceptTrc21Tx_WhenTokenCapacityCoversCost()
    {
        XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader = new();
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create(
            blocksToAdd: 0,
            useHotStuffModule: false,
            configurer: builder => builder.RegisterInstance(trc21Reader).As<ITrc21StateReader>().SingleInstance());

        chain.ChangeReleaseSpec(spec =>
        {
            spec.IsTipTrc21FeeEnabled = true;
            spec.BlockNumberGas50x = long.MaxValue;
        });

        const long gasLimit = 100_000;
        UInt256 requiredCost = (UInt256)XdcConstants.Trc21GasPrice * (UInt256)gasLimit;
        trc21Reader.FeeCapacities[XdcTxPoolTestHelper.TokenAddress] = requiredCost + 1;
        trc21Reader.IsValid = true;

        PrivateKey sender = chain.RandomKeys[0];
        UInt256 nonce = chain.TxPool.GetLatestPendingNonce(sender.Address);
        Transaction tx = BuildSignedTx(chain, sender, XdcTxPoolTestHelper.TokenAddress, nonce, XdcConstants.Trc21GasPrice, gasLimit, 0);

        AcceptTxResult result = chain.TxPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public async Task SubmitTx_ShouldRejectTrc21Tx_WhenTokenCapacityIsInsufficient()
    {
        XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader = new();
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create(
            blocksToAdd: 0,
            useHotStuffModule: false,
            configurer: builder => builder.RegisterInstance(trc21Reader).As<ITrc21StateReader>().SingleInstance());

        chain.ChangeReleaseSpec(spec =>
        {
            spec.IsTipTrc21FeeEnabled = true;
            spec.BlockNumberGas50x = long.MaxValue;
        });

        trc21Reader.FeeCapacities[XdcTxPoolTestHelper.TokenAddress] = 1;
        trc21Reader.IsValid = true;

        PrivateKey sender = chain.RandomKeys[1];
        UInt256 nonce = chain.TxPool.GetLatestPendingNonce(sender.Address);
        Transaction tx = BuildSignedTx(chain, sender, XdcTxPoolTestHelper.TokenAddress, nonce, XdcConstants.Trc21GasPrice, 100_000, 0);

        AcceptTxResult result = chain.TxPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

        Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
    }

    private static Transaction BuildSignedTx(
        XdcTestBlockchain chain,
        PrivateKey sender,
        Address to,
        UInt256 nonce,
        long gasPrice,
        long gasLimit,
        ulong value)
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(sender.Address, to, gasPrice, gasLimit, value, nonce);

        Signer signer = new(chain.SpecProvider.ChainId, sender, NullLogManager.Instance);
        signer.Sign(tx);
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
