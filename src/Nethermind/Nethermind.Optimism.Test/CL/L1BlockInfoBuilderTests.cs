// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class L1BlockInfoBuilderTests
{
    [Test]
    public void MainnetSystemTx()
    {
        byte[] data = Convert.FromHexString(
            "440a5e200000146b000f79c5000000000000000100000000678f36fb00000000014aac0100000000000000000000000000000000000000000000000000000001763888f90000000000000000000000000000000000000000000000000000000006ec6da8e3c9e07f6666c25368058388c7be8ce0e66212ea2c237514932fbbbd58e2dd800000000000000000000000006887246668a3b87f54deb3b94ba47a6f63f32985");
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(data, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(l1BlockInfo.BlockHash, Is.EqualTo(new Hash256("0xe3c9e07f6666c25368058388c7be8ce0e66212ea2c237514932fbbbd58e2dd80")));
            Assert.That(l1BlockInfo.Number, Is.EqualTo(21670913));
            Assert.That(l1BlockInfo.BaseFee, Is.EqualTo((UInt256)6278383865));
        }

        CLChainSpecEngineParameters parameters = new() { OptimismPortalProxy = TestItem.AddressA };
        DepositTransactionBuilder depositTransactionBuilder = new(1, parameters);
        Transaction tx = depositTransactionBuilder.BuildL1InfoTransaction(l1BlockInfo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.Data, Is.SequenceEqualTo(data));
            Assert.That(tx.SourceHash, Is.EqualTo(new Hash256("0x0a17e1f9443295ceee6678f1fc70aa6e45d2988cfbe062ec28db9f1e5fcb469d")));
        }
    }

    [TestCase(MainnetSpecProvider.CancunBlockTimestamp, "32594247408339235274")]
    [TestCase(MainnetSpecProvider.PragueBlockTimestamp - 1, "32594247408339235274")]
    [TestCase(MainnetSpecProvider.PragueBlockTimestamp, "10203738961070")]
    [TestCase(MainnetSpecProvider.BPO1BlockTimestamp, "63863970")]
    [TestCase(MainnetSpecProvider.BPO2BlockTimestamp, "375995")]
    public void FromL1BlockAndSystemConfig_uses_blob_fee_update_fraction_of_l1_block(ulong l1Timestamp, string expectedBlobBaseFee)
    {
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(
            CreateL1Block(l1Timestamp, 150_000_000), new SystemConfig(), 0, MainnetSpecProvider.Instance);

        Assert.That(l1BlockInfo.BlobBaseFee, Is.EqualTo(UInt256.Parse(expectedBlobBaseFee)));
    }

    [Test]
    public void FromL1BlockAndSystemConfig_throws_when_blob_base_fee_overflows() =>
        Assert.That(
            () => L1BlockInfoBuilder.FromL1BlockAndSystemConfig(
                CreateL1Block(MainnetSpecProvider.BPO2BlockTimestamp, ulong.MaxValue), new SystemConfig(), 0, MainnetSpecProvider.Instance),
            Throws.InvalidOperationException);

    private static L1Block CreateL1Block(ulong timestamp, ulong excessBlobGas) => new()
    {
        Hash = TestItem.KeccakA,
        Number = 1,
        Timestamp = timestamp,
        BaseFeePerGas = 1,
        ExcessBlobGas = excessBlobGas,
    };
}
